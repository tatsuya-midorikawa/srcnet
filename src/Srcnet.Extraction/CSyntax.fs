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
open Srcnet.Core.Graph
open Srcnet.Text
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

let private staticKeyword = "static"B
let private ifndefDirective = "#ifndef"B

/// テスト定義を作る Google Test 系のマクロ。docs/extraction.md 7 を参照。
let private testMacros =
  [| "TEST"; "TEST_F"; "TEST_P"; "TYPED_TEST"; "TYPED_TEST_P" |]

let private isTestMacro (name: string) =
  Array.exists (fun (macro: string) -> String.Equals(macro, name, StringComparison.Ordinal)) testMacros

let private normalizeName (raw: string) =
  if raw.Length = 0 then ""
  else
    let normalized = Unicode.normalize raw

    if normalized.Length <= Limits.MaxNameLength then normalized
    else normalized.Substring(0, Limits.MaxNameLength)

/// 条件式の原文を、空白を畳んだ 1 行の文字列にする。評価はしない。
let private conditionText (source: byte[]) (node: SyntaxTree.Node) =
  let start = node.StartByte
  let finish = min node.EndByte (min source.Length (start + Limits.MaxConditionTextLength * 4))
  let raw = if finish <= start then "" else Encoding.UTF8.GetString(source, start, finish - start)
  let builder = StringBuilder(min raw.Length Limits.MaxConditionTextLength)
  let mutable previousBlank = false
  let mutable index = 0

  while index < raw.Length && builder.Length < Limits.MaxConditionTextLength do
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
let rec private innermostDeclarator (node: SyntaxTree.Node) (budget: int) =
  if budget <= 0 then ValueNone
  else
    match node.Kind with
    | "identifier"
    | "field_identifier"
    | "type_identifier"
    | "qualified_identifier"
    | "destructor_name"
    | "operator_name" -> ValueSome node
    | "pointer_declarator"
    | "array_declarator"
    | "function_declarator"
    | "reference_declarator"
    | "parenthesized_declarator"
    | "init_declarator"
    | "attributed_declarator" ->
      match node.ChildByFieldUtf8 fieldDeclarator with
      | ValueSome child -> innermostDeclarator child (budget - 1)
      | ValueNone ->
        // `parenthesized_declarator` は `declarator` フィールドを持たない。
        let mutable found = ValueNone
        let mutable index = 0

        while found.IsNone && index < node.NamedChildCount do
          match node.NamedChild index with
          | ValueSome child ->
            match innermostDeclarator child (budget - 1) with
            | ValueSome inner -> found <- ValueSome inner
            | ValueNone -> ()
          | ValueNone -> ()

          index <- index + 1

        found
    | _ -> ValueNone

/// 宣言子が関数を宣言しているか。
let rec private isFunctionDeclarator (node: SyntaxTree.Node) (budget: int) =
  if budget <= 0 then false
  else
    match node.Kind with
    | "function_declarator" -> true
    | "pointer_declarator"
    | "array_declarator"
    | "reference_declarator"
    | "parenthesized_declarator"
    | "init_declarator"
    | "attributed_declarator" ->
      match node.ChildByFieldUtf8 fieldDeclarator with
      | ValueSome child -> isFunctionDeclarator child (budget - 1)
      | ValueNone -> false
    | _ -> false

/// `Derived::f` のような修飾識別子から短い名前を取り出す。
let private shortNameOf (qualified: string) =
  let index = qualified.LastIndexOf("::", StringComparison.Ordinal)
  if index < 0 then qualified else qualified.Substring(index + 2)

/// 直下に `static` の記憶域クラス指定子を持つか。
let private hasStaticStorage (source: byte[]) (node: SyntaxTree.Node) =
  let mutable found = false
  let mutable index = 0

  while not found && index < node.NamedChildCount do
    match node.NamedChild index with
    | ValueSome child when child.Kind = "storage_class_specifier" ->
      if child.StartsWith(ReadOnlySpan source, ReadOnlySpan staticKeyword) then found <- true
    | ValueSome _
    | ValueNone -> ()

    index <- index + 1

  found

/// 走査中のスコープ。囲みシンボルの添字と修飾接頭辞を持つ。
[<Struct>]
type private Scope = { SymbolIndex: int; Prefix: string }

/// 前処理条件の 1 枠。枝ごとの条件と、外側との合成条件を分けて持つ。
///
/// 分けて持つのは `#else` のためである。`#else` の条件は「直前の枝の否定」であり、
/// 合成条件だけを持っていると外側の条件まで否定してしまう。
[<Struct>]
type private ConditionFrame =
  { Own: string
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
  let mutable index = 0

  while index < condition.Length do
    let c = condition[index]

    if Char.IsLetter c || c = '_' then
      let start = index

      while index < condition.Length
            && (Char.IsLetterOrDigit condition[index] || condition[index] = '_') do
        index <- index + 1

      let word = condition.Substring(start, index - start)

      if word <> "defined" && not (into.Contains word) then into.Add word
    else index <- index + 1

/// C / C++ の構文木からシンボルと参照候補を抽出する。
///
/// `logicalPath` は内部リンケージのシンボルを翻訳単位ごとに区別するために使う
/// （docs/extraction.md 4.2）。
let run
  (language: Language)
  (logicalPath: string)
  (source: byte[])
  (tree: SyntaxTree.Tree)
  (inheritedFlags: NodeFlags)
  : SyntaxResult =

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
    if scopes.Count = 0 then { SymbolIndex = -1; Prefix = "" } else scopes.Peek()

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
      let normalized = normalizeName qualified
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
        if extraFlags.HasFlag NodeFlags.InternalLinkage && scope.Prefix = "" then
          logicalPath + "::" + scoped
        else scoped

      addQualified kind name qualified node.StartLine node.EndLine node.StartByte node.EndByte extraFlags doc

  let addReference (sourceIndex: int) (kind: EdgeKind) (target: string) (qualifier: string) (node: SyntaxTree.Node) (confidence: Confidence) =
    let normalized = normalizeName target

    if normalized <> "" then
      if references.Count >= Limits.MaxReferencesPerFile then truncated <- true
      else
        references.Add
          { Source = sourceIndex
            Kind = kind
            Target = normalized
            Qualifier = qualifier
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
      for index in node.NamedChildCount - 1 .. -1 .. 0 do
        match node.NamedChild index with
        | ValueSome child -> pending.Push(Visit(child, depth + 1))
        | ValueNone -> ()
    else truncated <- true

  /// 新しい条件の枝へ入る。`negatePrevious` は `#else` / `#elif` のための指定。
  let enterCondition (own: string) (negatePrevious: bool) =
    if conditions.Count >= Limits.MaxConditionDepth || conditionCount >= Limits.MaxConditionsPerFile then
      truncated <- true
    else
      conditionCount <- conditionCount + 1

      // `#else` は「直前の枝の否定」であり、外側の条件は否定しない。
      let struct (outer, effective) =
        if negatePrevious && conditions.Count > 0 then
          let parent = conditions.Peek()

          let negated =
            if own = "" then $"!({parent.Own})" else $"!({parent.Own}) && {own}"

          struct (parent.Outer, negated)
        else struct (currentCondition (), own)

      let combinedRaw =
        if outer = "" then effective
        elif effective = "" then outer
        else outer + " && " + effective

      let combined =
        if combinedRaw.Length <= Limits.MaxConditionTextLength then combinedRaw
        else combinedRaw.Substring(0, Limits.MaxConditionTextLength)

      pending.Push LeaveCondition

      conditions.Push
        { Own = effective
          Outer = outer
          Combined = combined }

  pending.Push(Visit(SyntaxTree.root tree, 0))

  while pending.Count > 0 do
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
      let negated = node.StartsWith(ReadOnlySpan source, ReadOnlySpan ifndefDirective)

      let own =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome name ->
          let identifier = name.Text source
          if negated then $"!defined({identifier})" else $"defined({identifier})"
        | ValueNone -> ""

      enterCondition own false
      pushChildren node depth

    | "preproc_if" ->
      let own =
        match node.ChildByFieldUtf8 fieldCondition with
        | ValueSome condition -> conditionText source condition
        | ValueNone -> ""

      enterCondition own false
      pushChildren node depth

    | "preproc_elif" ->
      let own =
        match node.ChildByFieldUtf8 fieldCondition with
        | ValueSome condition -> conditionText source condition
        | ValueNone -> ""

      enterCondition own true
      pushChildren node depth

    | "preproc_elifdef" ->
      let own =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome name -> $"defined({name.Text source})"
        | ValueNone -> ""

      enterCondition own true
      pushChildren node depth

    | "preproc_else" ->
      enterCondition "" true
      pushChildren node depth

    | "namespace_definition" ->
      let name =
        match node.ChildByFieldUtf8 fieldName with
        | ValueSome identifier -> identifier.Text source
        | ValueNone -> ""

      if name = "" then
        // 無名名前空間には名前を付けない。内側の宣言は内部リンケージを持つ。
        pushChildren node depth
      else
        let doc = takeDoc node
        let scope = currentScope ()
        let index = addSymbol Module name node NodeFlags.Definition doc
        let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
        pending.Push LeaveScope
        pushChildren node depth
        scopes.Push { SymbolIndex = index; Prefix = prefix }

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
          let scope = currentScope ()
          addReference scope.SymbolIndex References (nameNode.Text source) scope.Prefix nameNode Extracted
          pushChildren node depth
        | ValueSome _ ->
          let name = nameNode.Text source
          let doc = takeDoc node
          let scope = currentScope ()
          let index = addSymbol Type name node NodeFlags.Definition doc

          for childIndex in 0 .. node.NamedChildCount - 1 do
            match node.NamedChild childIndex with
            | ValueSome child when child.Kind = "base_class_clause" ->
              for baseIndex in 0 .. child.NamedChildCount - 1 do
                match child.NamedChild baseIndex with
                | ValueSome baseNode when
                  baseNode.Kind = "type_identifier" || baseNode.Kind = "qualified_identifier"
                  ->
                  addReference index Inherits (baseNode.Text source) scope.Prefix baseNode Extracted
                | ValueSome _
                | ValueNone -> ()
            | ValueSome _
            | ValueNone -> ()

          let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
          pending.Push LeaveScope
          pushChildren node depth
          scopes.Push { SymbolIndex = index; Prefix = prefix }

    | "enum_specifier" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueNone -> pushChildren node depth
      | ValueSome nameNode ->
        let name = nameNode.Text source
        let doc = takeDoc node
        let scope = currentScope ()
        let index = addSymbol Type name node NodeFlags.Definition doc
        let prefix = if scope.Prefix = "" then name else scope.Prefix + "::" + name
        pending.Push LeaveScope
        pushChildren node depth
        scopes.Push { SymbolIndex = index; Prefix = prefix }

    | "enumerator" ->
      match node.ChildByFieldUtf8 fieldName with
      | ValueSome nameNode ->
        addSymbol Constant (nameNode.Text source) node NodeFlags.Definition ByteRange.empty
        |> ignore
      | ValueNone -> ()

    | "type_definition" ->
      // `typedef struct { ... } Name;`。宣言される名前は末尾の `type_identifier`。
      let doc = takeDoc node

      // `typedef struct Point { ... } Point;` では内側の指定子が同じ名前で定義済みになる。
      // 二重に登録すると、同じ型が序数違いで 2 つ現れる。
      let mutable inner = ""
      let mutable childIndex = 0

      while childIndex < node.NamedChildCount do
        match node.NamedChild childIndex with
        | ValueSome child when
          child.Kind = "struct_specifier" || child.Kind = "union_specifier" || child.Kind = "enum_specifier"
          ->
          match child.ChildByFieldUtf8 fieldName with
          | ValueSome innerName -> inner <- innerName.Text source
          | ValueNone -> ()
        | ValueSome _
        | ValueNone -> ()

        childIndex <- childIndex + 1

      let mutable index = node.NamedChildCount - 1
      let mutable named = false

      while not named && index >= 0 do
        match node.NamedChild index with
        | ValueSome child when child.Kind = "type_identifier" ->
          let name = child.Text source
          named <- true

          if not (String.Equals(name, inner, StringComparison.Ordinal)) then
            addSymbol Type name node NodeFlags.Definition doc |> ignore
        | ValueSome _
        | ValueNone -> ()

        index <- index - 1

      pushChildren node depth

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
        match innermostDeclarator declarator DeclaratorBudget with
        | ValueNone -> pushChildren node depth
        | ValueSome nameNode ->
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

            pending.Push LeaveScope
            pushChildren node depth
            scopes.Push { SymbolIndex = index; Prefix = qualified }
          else
            let linkage =
              if hasStaticStorage source node then NodeFlags.InternalLinkage
              else NodeFlags.None

            // `Derived::f` のように綴り自体が修飾を含む場合は、それを修飾名の基礎にする。
            let isQualifiedSpelling = raw.Contains("::", StringComparison.Ordinal)

            let qualified =
              let scoped = if scope.Prefix = "" then raw else scope.Prefix + "::" + raw

              if not isQualifiedSpelling && linkage = NodeFlags.InternalLinkage && scope.Prefix = "" then
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
            pending.Push LeaveScope
            pushChildren node depth
            scopes.Push { SymbolIndex = index; Prefix = prefix }

    | "field_declaration" ->
      match node.ChildByFieldUtf8 fieldDeclarator with
      | ValueNone -> pushChildren node depth
      | ValueSome declarator ->
        let doc = takeDoc node
        let isFunction = isFunctionDeclarator declarator DeclaratorBudget

        match innermostDeclarator declarator DeclaratorBudget with
        | ValueNone -> ()
        | ValueSome nameNode ->
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
      let isStatic = hasStaticStorage source node
      let mutable childIndex = 0

      while childIndex < node.NamedChildCount do
        match node.NamedChild childIndex with
        | ValueSome child when
          child.Kind = "init_declarator"
          || child.Kind = "function_declarator"
          || child.Kind = "pointer_declarator"
          || child.Kind = "array_declarator"
          || child.Kind = "reference_declarator"
          || child.Kind = "identifier"
          ->
          let isFunction = isFunctionDeclarator child DeclaratorBudget

          match innermostDeclarator child DeclaratorBudget with
          | ValueSome nameNode ->
            let raw = nameNode.Text source
            let linkage = if isStatic then NodeFlags.InternalLinkage else NodeFlags.None

            if isFunction then
              addSymbol Function (shortNameOf raw) node (NodeFlags.DeclarationOnly ||| linkage) doc
              |> ignore
            elif scope.SymbolIndex < 0 then
              // ファイル直下の宣言だけを大域変数として扱う。関数本体の局所変数は載せない。
              addSymbol Variable raw node (NodeFlags.Definition ||| linkage) doc |> ignore
          | ValueNone -> ()
        | ValueSome _
        | ValueNone -> ()

        childIndex <- childIndex + 1

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
