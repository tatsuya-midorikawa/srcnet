/// T2 C / C++ 構文抽出。
///
/// tree-sitter の `c` / `cpp` 文法が返した木から、グラフのノードと参照候補を作る
/// （docs/extraction.md 4）。プリプロセッサは展開せず、条件の枝はすべて抽出対象にする。
///
/// 走査は `SyntaxTree.descendants` ではなく専用の明示的スタックで行う。囲みスコープと
/// 前処理条件を「入る／出る」の対で追う必要があり、単純な前順列挙では表現できないためである。
module Srcnet.Extraction.CSyntax

open System
open System.Collections.Generic
open System.Text
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Extraction.Model

/// 抽出結果。
type SyntaxResult =
  { Symbols: ExtractedSymbol[]
    References: ExtractedReference[]
    /// 上限に達して打ち切った。
    Truncated: bool
    /// 構文誤りを含む部分木の数。抽出は止めず、件数だけ数える。
    ErrorNodes: int }

// 役割名は固定なので UTF-8 バイト列を先に作り、ノードごとの変換を避ける。
let private fieldName = "name"B
let private fieldDeclarator = "declarator"B
let private fieldBody = "body"B
let private fieldPath = "path"B
let private fieldCondition = "condition"B
let private fieldType = "type"B
let private fieldParameters = "parameters"B
let private fieldFunction = "function"B
let private fieldAlternative = "alternative"B

let private staticKeyword = "static"B
let private externKeyword = "extern"B

/// テスト定義を作る Google Test 系のマクロ。docs/extraction.md 7 を参照。
let private testMacros =
  [| "TEST"; "TEST_F"; "TEST_P"; "TYPED_TEST"; "TYPED_TEST_P" |]

let private isTestMacro (name: string) =
  Array.exists (fun (macro: string) -> String.Equals(macro, name, StringComparison.Ordinal)) testMacros

/// 条件式の原文を、空白を畳んだ 1 行の文字列にする。評価はしない。
let private conditionText (source: byte[]) (node: SyntaxTree.Node) =
  let start = node.StartByte
  let finish = min node.EndByte (min source.Length (start + Limits.MaxConditionTextLength * 4))
  let raw = if finish <= start then "" else Encoding.UTF8.GetString(source, start, finish - start)
  let builder = StringBuilder(min raw.Length Limits.MaxConditionTextLength)
  let mutable previousBlank = false
  let mutable index = 0

  while index < raw.Length && builder.Length <= Limits.MaxConditionTextLength do
    let c = raw[index]

    if c = '\n' || c = '\r' || c = '\t' || c = ' ' then
      if not previousBlank && builder.Length > 0 then builder.Append ' ' |> ignore
      previousBlank <- true
    else
      builder.Append c |> ignore
      previousBlank <- false

    index <- index + 1

  builder.ToString().TrimEnd()

/// 宣言子の入れ子の上限。病的に深い宣言でも有限時間で終わらせる。
[<Literal>]
let private DeclaratorBudget = 64

/// 宣言子から名前を持つノードまで降りる。
///
/// `int *f(void)[3]` のように、宣言子はポインタ・配列・関数で入れ子になる。
/// 名前は最も内側にあるため、既知の包み方を順に剥がす。
let rec private declaratorInfo (node: SyntaxTree.Node) (budget: int) (isFunction: bool) =
  if budget <= 0 then ValueNone
  else
    match node.Kind with
    | "identifier"
    | "field_identifier"
    | "type_identifier"
    | "qualified_identifier"
    | "destructor_name"
    | "operator_name" -> ValueSome(struct (node, isFunction))
    | "pointer_declarator"
    | "array_declarator"
    | "function_declarator"
    | "reference_declarator"
    | "parenthesized_declarator"
    | "init_declarator"
    | "attributed_declarator" ->
      // The operator nearest the identifier determines whether this is a
      // function or an object, not an outer function-pointer return type.
      let nextIsFunction =
        match node.Kind with
        | "function_declarator" -> true
        | "pointer_declarator"
        | "array_declarator"
        | "reference_declarator" -> false
        | _ -> isFunction

      match node.ChildByFieldUtf8 fieldDeclarator with
      | ValueSome child -> declaratorInfo child (budget - 1) nextIsFunction
      | ValueNone ->
        // `parenthesized_declarator` は `declarator` フィールドを持たない。
        let mutable found = ValueNone
        let mutable index = 0

        while found.IsNone && index < node.NamedChildCount do
          match node.NamedChild index with
          | ValueSome child ->
            match declaratorInfo child (budget - 1) nextIsFunction with
            | ValueSome inner -> found <- ValueSome inner
            | ValueNone -> ()
          | ValueNone -> ()

          index <- index + 1

        found
    | _ -> ValueNone

let private isDeclarator (node: SyntaxTree.Node) =
  match node.Kind with
  | "identifier"
  | "field_identifier"
  | "qualified_identifier"
  | "destructor_name"
  | "operator_name"
  | "pointer_declarator"
  | "array_declarator"
  | "function_declarator"
  | "reference_declarator"
  | "parenthesized_declarator"
  | "init_declarator"
  | "attributed_declarator" -> true
  | _ -> false

/// `Derived::f` のような修飾識別子から短い名前を取り出す。
let private shortNameOf (qualified: string) =
  let index = qualified.LastIndexOf("::", StringComparison.Ordinal)
  if index < 0 then qualified else qualified.Substring(index + 2)

/// 直下に指定された記憶域クラス指定子を持つか。
let private hasStorage (keyword: byte[]) (source: byte[]) (children: SyntaxTree.Node[]) =
  let mutable found = false
  let mutable index = 0

  while not found && index < children.Length do
    let child = children[index]

    if child.Kind = "storage_class_specifier" then
      found <- child.Slice(ReadOnlySpan source).SequenceEqual(ReadOnlySpan keyword)

    index <- index + 1

  found

let private isNegatedDirective (source: byte[]) (node: SyntaxTree.Node) =
  let text = node.Slice(ReadOnlySpan source)
  let mutable index = min 1 text.Length

  while index < text.Length && (text[index] = ' 'B || text[index] = '\t'B) do
    index <- index + 1

  text.Slice(index).StartsWith("ifndef"B) || text.Slice(index).StartsWith("elifndef"B)

let private isTypeDeclaration (node: SyntaxTree.Node) =
  match node.Parent with
  | ValueSome parent ->
    match parent.Kind with
    | "translation_unit"
    | "declaration_list"
    | "field_declaration_list"
    | "preproc_if"
    | "preproc_ifdef"
    | "preproc_elif"
    | "preproc_elifdef"
    | "preproc_else" -> true
    | "declaration"
    | "field_declaration" -> ValueOption.isNone (parent.ChildByFieldUtf8 fieldDeclarator)
    | _ -> false
  | ValueNone -> false

/// 走査中のスコープ。囲みシンボルの添字と修飾接頭辞を持つ。
[<Struct>]
type private Scope =
  { SymbolIndex: int
    Prefix: string
    Kind: NodeKind
    InternalLinkage: bool }

/// 前処理条件の 1 枠。枝ごとの条件と、外側との合成条件を分けて持つ。
///
/// `Remaining` は先行するすべての枝を除外する条件。外側の条件とは別に保持する。
[<Struct>]
type private ConditionFrame =
  { Remaining: string
    Outer: string
    Combined: string }

/// 走査の作業項目。
[<NoEquality; NoComparison>]
type private Step =
  | Visit of node: SyntaxTree.Node * depth: int
  | LeaveScope
  | LeaveCondition

/// 条件式の原文から識別子を取り出す。`defined` は演算子であって構成シンボルではない。
let private configIdentifiers (condition: string) (into: List<string>) =
  let identifierWidth (first: bool) (index: int) =
    let rune = Rune.GetRuneAt(condition, index)
    let category = Rune.GetUnicodeCategory rune

    if rune.Value = int '_' || Rune.IsLetter rune
       || (not first
           && (Rune.IsDigit rune
               || category = Globalization.UnicodeCategory.NonSpacingMark
               || category = Globalization.UnicodeCategory.SpacingCombiningMark
               || category = Globalization.UnicodeCategory.ConnectorPunctuation)) then
      rune.Utf16SequenceLength
    else 0

  let mutable index = 0

  while index < condition.Length do
    let c = condition[index]

    if c = '\'' || c = '"' then
      let quote = c
      index <- index + 1
      let mutable closed = false

      while index < condition.Length && not closed do
        if condition[index] = '\\' then index <- min condition.Length (index + 2)
        else
          closed <- condition[index] = quote
          index <- index + 1
    elif Char.IsDigit c then
      index <- index + 1

      while index < condition.Length
            && (Char.IsLetterOrDigit condition[index]
                || condition[index] = '_'
                || condition[index] = '.'
                || condition[index] = '\'') do
        index <- index + 1
    else
      let width = identifierWidth true index

      if width > 0 then
        let start = index
        index <- index + width
        let mutable more = true

        while index < condition.Length && more do
          let width = identifierWidth false index
          if width = 0 then more <- false else index <- index + width

        let word = normalizeName (condition.Substring(start, index - start))

        if word <> "defined" && not (into.Contains word) then into.Add word
      else
        index <- index + (Rune.GetRuneAt(condition, index)).Utf16SequenceLength

/// C / C++ の構文木からシンボルと参照候補を抽出する。
///
/// `logicalPath` は内部リンケージのシンボルを翻訳単位ごとに区別するために使う
/// （docs/extraction.md 4.2）。
let runWithCancellation
  (language: Language)
  (logicalPath: string)
  (source: byte[])
  (tree: SyntaxTree.Tree)
  (inheritedFlags: NodeFlags)
  (cancellation: CancellationToken)
  : SyntaxResult =

  cancellation.ThrowIfCancellationRequested()
  let symbols = List<ExtractedSymbol>()
  let references = List<ExtractedReference>()
  let ordinals = Dictionary<struct (byte * string), uint32>()
  let scopes = Stack<Scope>()
  let conditions = Stack<ConditionFrame>()
  let pending = Stack<Step>()

  let mutable lastComment = ByteRange.empty
  let mutable lastCommentEnd = -1
  let mutable truncated = false
  let mutable errorNodes = 0
  let mutable conditionCount = 0

  let baseFlags =
    inheritedFlags &&& (NodeFlags.Vendored ||| NodeFlags.Generated ||| NodeFlags.Test)

  let currentScope () =
    if scopes.Count = 0 then
      { SymbolIndex = -1; Prefix = ""; Kind = File; InternalLinkage = false }
    else scopes.Peek()

  let isGlobalScope () =
    let kind = (currentScope ()).Kind
    kind = File || kind = Module

  let currentCondition () =
    if conditions.Count = 0 then "" else (conditions.Peek()).Combined

  // 同一ファイル内で `(種別, 修飾名)` が衝突する場合の序数。出現位置順に割り当てる。
  let nextOrdinal (kind: NodeKind) (qualified: string) =
    let key = struct (NodeKind.toCode kind, qualified)

    match ordinals.TryGetValue key with
    | true, existing ->
      ordinals[key] <- existing + 1u
      existing + 1u
    | false, _ ->
      ordinals[key] <- 0u
      0u

  /// 修飾名を明示してシンボルを追加する。追加できなければ -1 を返す。
  let addQualified (kind: NodeKind) (name: string) (qualified: string) (startLine: int) (endLine: int) (startByte: int) (endByte: int) (extraFlags: NodeFlags) (doc: ByteRange) =
    if name = "" || qualified = "" then -1
    elif symbols.Count >= Limits.MaxSymbolsPerFile then
      truncated <- true
      -1
    else
      let condition = currentCondition ()
      let scope = currentScope ()
      let linkage =
        if scope.InternalLinkage then NodeFlags.InternalLinkage
        else NodeFlags.None

      let normalized =
        if scope.InternalLinkage then normalizeName (logicalPath + "::" + qualified)
        else normalizeName qualified

      let index = symbols.Count

      symbols.Add
        { Kind = kind
          Name = normalizeName name
          QualifiedName = normalized
          Parent = (currentScope ()).SymbolIndex
          Ordinal = nextOrdinal kind normalized
          StartLine = startLine
          EndLine = endLine
          StartByte = startByte
          EndByte = endByte
          Flags =
            baseFlags
            ||| extraFlags
            ||| linkage
            ||| (if condition = "" then NodeFlags.None else NodeFlags.Conditional)
          Doc = doc
          Condition = condition }

      index

  /// 囲みスコープに従って修飾名を組み立て、シンボルを追加する。
  let addSymbol (kind: NodeKind) (name: string) (node: SyntaxTree.Node) (extraFlags: NodeFlags) (doc: ByteRange) =
    if name = "" then -1
    else
      let scope = currentScope ()
      let scoped = if scope.Prefix = "" then name else scope.Prefix + "::" + name

      // 内部リンケージのシンボルは、修飾名だけでは別翻訳単位の同名と区別できない。
      // 論理パスを修飾へ含めることで、解決規則が翻訳単位を跨がないようにする。
      let qualified =
        if extraFlags.HasFlag NodeFlags.InternalLinkage && not scope.InternalLinkage then
          logicalPath + "::" + scoped
        else scoped

      addQualified kind (shortNameOf name) qualified node.StartLine node.EndLine node.StartByte node.EndByte extraFlags doc

  let addReference (sourceIndex: int) (kind: EdgeKind) (target: string) (qualifier: string) (node: SyntaxTree.Node) (confidence: Confidence) =
    let normalized = normalizeName target

    if normalized <> "" then
      if references.Count >= Limits.MaxReferencesPerFile then truncated <- true
      else
        references.Add
          { Source = sourceIndex
            Kind = kind
            Target = normalized
            Qualifier = normalizeName qualifier
            Language = language
            StartByte = node.StartByte
            EndByte = node.EndByte
            Line = node.StartLine
            Stage = 0uy
            Confidence = confidence }

  /// 直前のコメントを `docRef` として使えるか判定する。
  /// 定義との間が空白と改行だけであることを確かめ、離れたコメントを結び付けない。
  let takeDoc (node: SyntaxTree.Node) =
    if lastCommentEnd < 0 || lastCommentEnd > node.StartByte then ByteRange.empty
    else
      let mutable index = lastCommentEnd
      let mutable onlyBlank = true

      while onlyBlank && index < node.StartByte && index < source.Length do
        let b = source[index]
        if b <> 0x20uy && b <> 0x09uy && b <> 0x0Auy && b <> 0x0Duy then onlyBlank <- false
        index <- index + 1

      if onlyBlank then lastComment else ByteRange.empty

  /// 子を原文の出現順に処理するため、逆順に積む。
  let pushChildren (node: SyntaxTree.Node) (depth: int) =
    if depth < SyntaxTree.MaxWalkDepth then
      let children = node.NamedChildren cancellation

      for index in children.Length - 1 .. -1 .. 0 do
        pending.Push(Visit(children[index], depth + 1))
    else truncated <- true

  let enterScope (node: SyntaxTree.Node) depth symbolIndex prefix =
    let outer = currentScope ()
    let kind =
      match node.Kind with
      | "namespace_definition" -> Module
      | "function_definition" -> Function
      | _ -> Type

    let internalLinkage =
      outer.InternalLinkage
      || (kind = Module && ValueOption.isNone (node.ChildByFieldUtf8 fieldName))

    pending.Push LeaveScope
    pushChildren node depth
    scopes.Push
      { SymbolIndex = symbolIndex
        Prefix = normalizeName prefix
        Kind = kind
        InternalLinkage = internalLinkage }

  let boundedCondition text =
    if String.length text > Limits.MaxConditionTextLength then truncated <- true
    truncateText Limits.MaxConditionTextLength text

  /// 新しい条件の枝へ入る。`negatePrevious` は `#else` / `#elif` のための指定。
  let enterCondition (own: string) (negatePrevious: bool) =
    if conditions.Count >= Limits.MaxConditionDepth || conditionCount >= Limits.MaxConditionsPerFile then
      truncated <- true
      false
    else
      conditionCount <- conditionCount + 1
      let own = boundedCondition own

      // Exclude every earlier branch without negating the enclosing condition.
      let struct (outer, effective, remaining) =
        if negatePrevious && conditions.Count > 0 then
          let parent = conditions.Peek()
          let effective = if own = "" then parent.Remaining else $"{parent.Remaining} && ({own})"
          let remaining = if own = "" then "" else $"{parent.Remaining} && !({own})"
          struct (parent.Outer, effective, remaining)
        else struct (currentCondition (), own, $"!({own})")

      let combinedRaw =
        if outer = "" then effective
        elif effective = "" then outer
        else $"({outer}) && ({effective})"

      pending.Push LeaveCondition

      conditions.Push
        { Remaining = boundedCondition remaining
          Outer = outer
          Combined = boundedCondition combinedRaw }

      true

  pending.Push(Visit(SyntaxTree.root tree, 0))

  while pending.Count > 0 do
    cancellation.ThrowIfCancellationRequested()
    match pending.Pop() with
    | LeaveScope -> if scopes.Count > 0 then scopes.Pop() |> ignore
    | LeaveCondition -> if conditions.Count > 0 then conditions.Pop() |> ignore
    | Visit(node, depth) ->

    if node.HasError then errorNodes <- errorNodes + 1

    match node.Kind with
    | "comment" ->
      lastComment <- ByteRange.create node.StartByte node.EndByte
      lastCommentEnd <- node.EndByte

    | "preproc_include" ->
      // `<x.h>` と `"x.h"` のどちらでも、綴りだけを記録する。解決は M3。
      let spelling =
        match node.ChildByFieldUtf8 fieldPath with
        | ValueSome path ->
          let raw = path.Text source

          if raw.Length >= 2 && (raw[0] = '<' || raw[0] = '"') then raw.Substring(1, raw.Length - 2)
          else raw
        | ValueNone -> ""

      addReference -1 Includes spelling "" node Extracted

    | "preproc_def" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueSome name ->
        let doc = takeDoc node
        addSymbol Constant (name.Text source) node NodeFlags.Definition doc |> ignore
      | ValueNone -> ()

    | "preproc_function_def" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueSome name ->
        let doc = takeDoc node
        addSymbol Macro (name.Text source) node NodeFlags.Definition doc |> ignore
      | ValueNone -> ()

    | "preproc_ifdef" ->
      let negated = isNegatedDirective source node

      let guardName =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome name -> name.Text source
        | ValueNone -> ""

      // インクルード ガードは構成条件ではない。`#ifndef X` の直後に `#define X` が
      // 続く形を条件として扱うと、ヘッダー内のすべてのシンボルが `Conditional` になり、
      // 実体のない `ConfigSymbol` と `GUARDED_BY` がヘッダーの数だけ増える。
      let isIncludeGuard =
        (language = Language.CHeader || language = Language.CppHeader)
        && depth = 1
        && negated
        && guardName <> ""
        && ValueOption.isNone (node.ChildByFieldUtf8 fieldAlternative)
        && (match node.NamedChild 1 with
            | ValueSome following when following.Kind = "preproc_def" ->
              match following.ChildByFieldUtf8 fieldName with
              | ValueSome definedName -> String.Equals(definedName.Text source, guardName, StringComparison.Ordinal)
              | ValueNone -> false
            | ValueSome _
            | ValueNone -> false)

      if isIncludeGuard then pushChildren node depth
      else
        let own =
          if guardName = "" then ""
          elif negated then $"!defined({guardName})"
          else $"defined({guardName})"

        if enterCondition own false then pushChildren node depth

    | "preproc_if" ->
      let own =
        match node.ChildByFieldUtf8 fieldCondition with
        | ValueSome condition -> conditionText source condition
        | ValueNone -> ""

      if enterCondition own false then pushChildren node depth

    | "preproc_elif" ->
      let own =
        match node.ChildByFieldUtf8 fieldCondition with
        | ValueSome condition -> conditionText source condition
        | ValueNone -> ""

      if enterCondition own true then pushChildren node depth

    | "preproc_elifdef" ->
      let own =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome name ->
          if isNegatedDirective source node then $"!defined({name.Text source})"
          else $"defined({name.Text source})"
        | ValueNone -> ""

      if enterCondition own true then pushChildren node depth

    | "preproc_else" ->
      if enterCondition "" true then pushChildren node depth

    | "namespace_definition" ->
      let name =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome identifier -> identifier.Text source
        | ValueNone -> ""

      if name = "" then
        // 無名名前空間には名前を付けない。内側の宣言は内部リンケージを持つ。
        let scope = currentScope ()
        enterScope node depth scope.SymbolIndex scope.Prefix
      else
        let doc = takeDoc node
        let scope = currentScope ()
        let index = addSymbol Module name node NodeFlags.Definition doc
        let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
        enterScope node depth index prefix

    | "class_specifier"
    | "struct_specifier"
    | "union_specifier" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueNone -> pushChildren node depth
      | ValueSome nameNode ->
        // 本体を持たない `struct Point p;` は型の使用であって定義ではない。
        let body = node.ChildByFieldUtf8 fieldBody

        match body with
        | ValueNone ->
          if isTypeDeclaration node then
            addSymbol Type (nameNode.Text source) node NodeFlags.DeclarationOnly (takeDoc node) |> ignore
          else
            let scope = currentScope ()
            addReference scope.SymbolIndex References (nameNode.Text source) scope.Prefix nameNode Extracted

          pushChildren node depth
        | ValueSome _ ->
          let name = nameNode.Text source
          let doc = takeDoc node
          let scope = currentScope ()
          let index = addSymbol Type name node NodeFlags.Definition doc

          for child in node.NamedChildren cancellation do
            if child.Kind = "base_class_clause" then
              for baseNode in child.NamedChildren cancellation do
                if baseNode.Kind = "type_identifier" || baseNode.Kind = "qualified_identifier" then
                  addReference index Inherits (baseNode.Text source) scope.Prefix baseNode Extracted

          let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
          enterScope node depth index prefix

    | "enum_specifier" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueNone -> pushChildren node depth
      | ValueSome nameNode ->
        if ValueOption.isSome (node.ChildByFieldUtf8 fieldBody) then
          let name = nameNode.Text source
          let doc = takeDoc node
          let scope = currentScope ()
          let index = addSymbol Type name node NodeFlags.Definition doc
          let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
          enterScope node depth index prefix
        else
          if isTypeDeclaration node then
            addSymbol Type (nameNode.Text source) node NodeFlags.DeclarationOnly (takeDoc node) |> ignore
          else
            let scope = currentScope ()
            addReference scope.SymbolIndex References (nameNode.Text source) scope.Prefix nameNode Extracted

          pushChildren node depth

    | "enumerator" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueSome nameNode ->
        addSymbol Constant (nameNode.Text source) node NodeFlags.Definition ByteRange.empty
        |> ignore
      | ValueNone -> ()

    | "type_definition" ->
      let doc = takeDoc node
      let children = node.NamedChildren cancellation
      let typeNode = node.ChildByFieldUtf8 fieldType
      let mutable inner = ""
      let mutable anonymous = false

      for child in children do
        if child.Kind = "struct_specifier" || child.Kind = "union_specifier" || child.Kind = "enum_specifier" then
          if ValueOption.isSome (child.ChildByFieldUtf8 fieldBody) then
            match child.ChildByFieldUtf8 fieldName with
            | ValueSome innerName -> inner <- innerName.Text source
            | ValueNone -> anonymous <- true

      let mutable aliasIndex = -1
      let mutable aliasPrefix = ""

      for child in children do
        let isType =
          match typeNode with
          | ValueSome underlying -> child.StartByte = underlying.StartByte && child.EndByte = underlying.EndByte
          | ValueNone -> false

        if not isType && (child.Kind = "type_identifier" || isDeclarator child) then
          match declaratorInfo child DeclaratorBudget false with
          | ValueSome(struct (nameNode, _)) ->
            let name = nameNode.Text source

            if not (String.Equals(name, inner, StringComparison.Ordinal)) then
              let index = addSymbol Type name node NodeFlags.Definition doc

              if anonymous && aliasIndex < 0 && child.Kind = "type_identifier" && index >= 0 then
                aliasIndex <- index
                let scope = currentScope ()
                aliasPrefix <- if scope.Prefix = "" then name else scope.Prefix + "::" + name
          | ValueNone -> ()

      if aliasIndex >= 0 then enterScope node depth aliasIndex aliasPrefix
      else pushChildren node depth

    | "alias_declaration" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueSome nameNode ->
        let doc = takeDoc node
        addSymbol Type (nameNode.Text source) node NodeFlags.Definition doc |> ignore
      | ValueNone -> ()

      pushChildren node depth

    | "function_definition" ->
      match node.ChildByFieldUtf8 fieldDeclarator with
      | ValueNone -> pushChildren node depth
      | ValueSome declarator ->
        match declaratorInfo declarator DeclaratorBudget false with
        | ValueNone -> pushChildren node depth
        | ValueSome(struct (nameNode, _)) ->
          let raw = nameNode.Text source
          let doc = takeDoc node
          let scope = currentScope ()

          if isTestMacro raw then
            // `TEST_F(Suite, Case) { ... }` は関数定義として解析される。
            // 引数はテストの識別であり、対象シンボルの推定材料になる。
            let caseName =
              match declarator.ChildByFieldUtf8 fieldParameters with
              | ValueSome parameters ->
                parameters.Text(source).Trim([| '('; ')'; ' ' |]).Replace(" ", "").Replace(",", ".")
              | ValueNone -> ""

            let name = if caseName = "" then raw else caseName
            let qualified = if scope.Prefix = "" then name else scope.Prefix + "::" + name

            let shortName =
              let dot = name.LastIndexOf '.'
              if dot < 0 then name else name.Substring(dot + 1)

            let index =
              addQualified
                Function
                shortName
                qualified
                node.StartLine
                node.EndLine
                node.StartByte
                node.EndByte
                (NodeFlags.Definition ||| NodeFlags.Test)
                doc

            // 命名規約からの推定であるため、対象への対応付けは `AMBIGUOUS` とする。
            if caseName <> "" && index >= 0 then
              let suite = (caseName.Split '.')[0]
              addReference index Tests suite scope.Prefix node Ambiguous

            enterScope node depth index qualified
          else
            let linkage =
              if isGlobalScope () && hasStorage staticKeyword source (node.NamedChildren cancellation) then
                NodeFlags.InternalLinkage
              else NodeFlags.None

            let qualified =
              let scoped = if scope.Prefix = "" then raw else scope.Prefix + "::" + raw

              if linkage = NodeFlags.InternalLinkage && not scope.InternalLinkage then
                logicalPath + "::" + scoped
              else scoped

            let index =
              addQualified
                Function
                (shortNameOf raw)
                qualified
                node.StartLine
                node.EndLine
                node.StartByte
                node.EndByte
                (NodeFlags.Definition ||| linkage)
                doc

            let prefix = if scope.Prefix = "" then raw else scope.Prefix + "::" + raw
            enterScope node depth index prefix

    | "field_declaration" ->
      let doc = takeDoc node

      for declarator in node.NamedChildren cancellation do
        if isDeclarator declarator then
          match declaratorInfo declarator DeclaratorBudget false with
          | ValueNone -> ()
          | ValueSome(struct (nameNode, isFunction)) ->
            let name = nameNode.Text source

            if isFunction then
              addSymbol Function name node NodeFlags.DeclarationOnly doc |> ignore
            else
              let index = addSymbol Field name node NodeFlags.Definition doc

              match node.ChildByFieldUtf8 fieldType with
              | ValueSome typeNode when typeNode.Kind = "type_identifier" && index >= 0 ->
                addReference index TypedAs (typeNode.Text source) (currentScope ()).Prefix typeNode Extracted
              | ValueSome _
              | ValueNone -> ()

      pushChildren node depth

    | "declaration" ->
      let doc = takeDoc node
      let scope = currentScope ()
      let children = node.NamedChildren cancellation
      let isStatic = hasStorage staticKeyword source children
      let isExtern = hasStorage externKeyword source children

      for child in children do
        if isDeclarator child then
          match declaratorInfo child DeclaratorBudget false with
          | ValueSome(struct (nameNode, isFunction)) ->
            let raw = nameNode.Text source
            let linkage =
              if isStatic && isGlobalScope () then NodeFlags.InternalLinkage
              else NodeFlags.None

            if isFunction then
              addSymbol Function raw node (NodeFlags.DeclarationOnly ||| linkage) doc
              |> ignore
            elif isGlobalScope () || (scope.Kind = Function && isStatic) then
              let flags =
                if isExtern && child.Kind <> "init_declarator" then NodeFlags.DeclarationOnly
                else NodeFlags.Definition

              addSymbol Variable raw node (flags ||| linkage) doc |> ignore
          | ValueNone -> ()

      pushChildren node depth

    | "call_expression" ->
      match node.ChildByFieldUtf8 fieldFunction with
      | ValueSome target ->
        // 関数ポインタや式経由の呼び出しは名前を持たない。名前がある場合だけ記録する。
        match target.Kind with
        | "identifier"
        | "field_expression"
        | "qualified_identifier"
        | "scoped_identifier" ->
          let scope = currentScope ()
          addReference scope.SymbolIndex Calls (target.Text source) scope.Prefix target Extracted
        | _ -> ()
      | ValueNone -> ()

      pushChildren node depth

    | _ -> pushChildren node depth

  // 条件下のシンボルへ `GUARDED_BY` を張る。
  //
  // 走査中ではなくここでまとめるのは、条件の合成が枝ごとに決まり、シンボルごとに
  // 保持した条件の原文だけで一意に定まるためである。木を再走査しない。
  let identifiers = List<string>()

  for index in 0 .. symbols.Count - 1 do
    cancellation.ThrowIfCancellationRequested()
    let symbol = symbols[index]

    if symbol.Condition <> "" then
      identifiers.Clear()
      configIdentifiers symbol.Condition identifiers

      for identifier in identifiers do
        if references.Count >= Limits.MaxReferencesPerFile then truncated <- true
        else
          references.Add
            { Source = index
              Kind = GuardedBy
              Target = identifier
              Qualifier = ""
              Language = language
              StartByte = symbol.StartByte
              EndByte = symbol.EndByte
              Line = symbol.StartLine
              Stage = 0uy
              Confidence = Extracted }

  { Symbols = symbols.ToArray()
    References = references.ToArray()
    Truncated = truncated
    ErrorNodes = errorNodes }

let run language logicalPath source tree inheritedFlags =
  runWithCancellation language logicalPath source tree inheritedFlags CancellationToken.None
