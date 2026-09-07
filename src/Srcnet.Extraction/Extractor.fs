/// 抽出パイプラインの入口。
///
/// 走査（段 1〜3）の 1 回読みの中で呼ばれ、読み取り済みのバイト列から段 4 の結果を作る
/// （docs/architecture.md 1）。ファイルを二度開かないことが、走査時間を倍にしない前提である。
///
/// T1 は常に走らせ、T2 が成功したファイルでは T1 の定義候補だけを捨てる。取り込みと
/// 根拠コメントは T1 の結果を使う（backlog 015）。段階の縮退は失敗ではなく診断である。
module Srcnet.Extraction.Extractor

open System
open System.Collections.Generic
open System.Threading
open Srcnet.Core.Graph
open Srcnet.Extraction.Model

/// 抽出の条件。実行ごとに固定で、ファイルごとに変えない。
type ExtractionOptions =
  { /// 要求した段階。実際に適用した段階は結果に載る。
    Tier: Tier
    /// 抽出対象にするファイルの上限。走査の `MaxFileSizeBytes` とは別に持つ。
    MaxExtractionBytes: int64
    /// 1 ファイルあたりの解析時間の上限（マイクロ秒）。
    TimeoutMicroseconds: uint64 }

module ExtractionOptions =

  let defaults =
    { Tier = Syntax
      MaxExtractionBytes = Limits.DefaultMaxExtractionBytes
      TimeoutMicroseconds = Parsing.DefaultTimeoutMicroseconds }

/// この言語を T2 で扱えるか。文法の有無だけで決まり、ファイルごとには変わらない。
let private syntaxLanguages = [| Language.C; Language.CHeader; Language.Cpp; Language.CppHeader |]

let supportsSyntax (language: Language) =
  Array.contains language syntaxLanguages && Parsing.supports language

/// テキストとして扱わない符号化か。抽出は復号済みの UTF-8 を前提にする。
let private isTextual (flags: NodeFlags) =
  not (flags.HasFlag NodeFlags.Binary) && not (flags.HasFlag NodeFlags.Skipped)

/// 根拠コメントから直後のシンボルへの `EXPLAINS` 候補を作る。
///
/// T1 は「直後のシンボル」を知らないため、併合後に位置関係だけで対応付ける。
/// 解決規則の段階は「同一ファイル」であり、docs/extraction.md 5.2 の 2 に当たる。
let private explainsReferences (language: Language) (symbols: ExtractedSymbol[]) (cancellation: CancellationToken) =
  let result = List<ExtractedReference>()
  let mutable cursor = 0

  for index in 0 .. symbols.Length - 1 do
    cancellation.ThrowIfCancellationRequested()
    let note = symbols[index]

    if note.Kind = Note then
      // T1 notes occupy ordered, non-overlapping line ranges. Never rescan a
      // run of comments for each preceding note.
      cursor <- max cursor (index + 1)

      while cursor < symbols.Length
            && (symbols[cursor].Kind = Note || symbols[cursor].StartByte < note.EndByte) do
        cursor <- cursor + 1

      if cursor < symbols.Length then
        result.Add
          { Source = index
            Kind = Explains
            Target = symbols[cursor].QualifiedName
            Qualifier = ""
            Language = language
            StartByte = note.StartByte
            EndByte = note.EndByte
            Line = note.StartLine
            Stage = 2uy
            Confidence = Extracted }

  result.ToArray()

/// 序数を最終的な並びで振り直す。
///
/// 抽出器ごとに割り当てると、T1 と T2 の結果を併合した時点で重複し得る。
/// 「同一ファイル内で `(種別, 修飾名)` が衝突する場合に出現位置順」という規則を、
/// 併合後の一意な並びに対して適用する（docs/graph-model.md 4.2）。
let private assignOrdinals (symbols: ExtractedSymbol[]) =
  let counters = Dictionary<struct (byte * string), uint32>()

  Array.map
    (fun (symbol: ExtractedSymbol) ->
      let key = struct (NodeKind.toCode symbol.Kind, symbol.QualifiedName)

      let ordinal =
        match counters.TryGetValue key with
        | true, existing ->
          counters[key] <- existing + 1u
          existing + 1u
        | false, _ ->
          counters[key] <- 0u
          0u

      { symbol with Ordinal = ordinal })
    symbols

/// ワーカーごとに 1 つ持つ抽出器。
///
/// 解析器の有無の判定と、縮退の記録をインスタンスに閉じ込める。判定をファイルごとに
/// 繰り返さず、縮退の診断も実行あたり 1 回にまとめられる（backlog 021）。
[<Sealed>]
type Extractor(options: ExtractionOptions) =
  let parserAvailable = Parsing.isAvailable.Value

  /// 実行を通じて T2 へ届かなかったファイルがあったか。診断をまとめるために持つ。
  let mutable degraded = false

  member _.Options = options

  member _.ParserAvailable = parserAvailable

  member _.Degraded = degraded

  /// 1 ファイルを抽出する。
  ///
  /// `source` は復号済み UTF-8 のバッファで、`length` バイトが内容である。
  /// バッファはワーカーが再利用するため、結果へ参照を残してはならない。
  member _.Extract
    (
      language: Language,
      logicalPath: string,
      source: byte[],
      length: int,
      fileFlags: NodeFlags,
      cancellation: CancellationToken
    ) : ExtractedFile =

    ArgumentNullException.ThrowIfNull source

    if length < 0 || length > source.Length then
      invalidArg (nameof length) "抽出長がバッファ長を超えています"

    cancellation.ThrowIfCancellationRequested()

    if options.Tier = Structure then ExtractedFile.empty Structure
    elif not (isTextual fileFlags) then ExtractedFile.skipped Structure NotText
    elif int64 length > options.MaxExtractionBytes then
      ExtractedFile.skipped Structure (TooLargeToExtract(int64 length))
    else

    // T1 は常に走らせる。T2 が成功しても、取り込みと根拠コメントはこちらを使う。
    let lineResult =
      LineScan.runWithCancellation language (ReadOnlySpan(source, 0, length)) fileFlags cancellation

    let generatedFlag =
      if lineResult.Generated then NodeFlags.Generated else NodeFlags.None

    let wantsSyntax = Tier.rank options.Tier >= Tier.rank Syntax
    let isSyntaxLanguage = Array.contains language syntaxLanguages

    let struct (tier, syntax, skipped) =
      if not wantsSyntax then struct (LineOriented, ValueNone, ValueNone)
      elif not isSyntaxLanguage then
        // T2 の対象言語でないファイルは、T1 が本来の段階である。縮退ではない。
        struct (LineOriented, ValueNone, ValueNone)
      elif not (Parsing.supports language) then
        // 対象言語なのに文法が無い。実行あたり 1 回にまとめて診断する。
        degraded <- true
        struct (LineOriented, ValueNone, ValueSome NoGrammar)
      else
        match Parsing.parseRange language source length options.TimeoutMicroseconds cancellation with
        | Error Parsing.Cancelled ->
          cancellation.ThrowIfCancellationRequested()
          struct (LineOriented, ValueNone, ValueSome ParseTimedOut)
        | Error Parsing.TimedOut -> struct (LineOriented, ValueNone, ValueSome ParseTimedOut)
        | Error(Parsing.ParserUnavailable detail) ->
          degraded <- true
          struct (LineOriented, ValueNone, ValueSome(ParseUnavailable detail))
        | Error(Parsing.GrammarUnavailable _) ->
          degraded <- true
          struct (LineOriented, ValueNone, ValueSome NoGrammar)
        | Error failure ->
          struct (LineOriented, ValueNone, ValueSome(ParseUnavailable(Parsing.ParseFailure.describe failure)))
        | Ok tree ->
          use tree = tree

          let result =
            CSyntax.runWithCancellation language logicalPath source tree (fileFlags ||| generatedFlag) cancellation

          struct (Syntax, ValueSome result, ValueNone)

    // T2 が成功したファイルでは、T1 の定義候補を捨てて二重定義を防ぐ。
    // 根拠コメント（`Note`）は構文木からは得られないため、常に T1 のものを使う。
    let lineSymbols =
      let kept =
        match syntax with
        | ValueSome _ -> lineResult.Symbols |> Array.filter (fun symbol -> symbol.Kind = Note)
        | ValueNone -> lineResult.Symbols

      // 生成物マーカーは走査の途中で見つかるため、行指向の抽出器はファイル全体の
      // 属性を最初から知らない。判明した属性をここで全シンボルへ配る（backlog 023）。
      if generatedFlag = NodeFlags.None then kept
      else kept |> Array.map (fun symbol -> { symbol with Flags = symbol.Flags ||| generatedFlag })

    let syntaxSymbols =
      match syntax with
      | ValueSome result -> result.Symbols
      | ValueNone -> Array.empty

    // 併合時に添字がずれるため、T1 側の参照元添字を先にずらしておく。
    // T1 の参照はファイル自身（-1）が発生元なので実際にはずれないが、
    // 将来 T1 がシンボルを発生元にした場合に破綻しないよう明示する。
    let offset = syntaxSymbols.Length

    let lineReferences =
      lineResult.References
      |> Array.map (fun reference ->
        if reference.Source < 0 then reference
        else { reference with Source = reference.Source + offset })

    // 取り込みは T1 の結果を使う（backlog 015）。T2 も同じ綴りを拾うため、
    // どちらかに寄せなければ 1 つの `#include` から 2 本の候補が出る。
    let syntaxReferences =
      match syntax with
      | ValueSome result ->
        result.References
        |> Array.filter (fun reference -> reference.Kind <> Includes && reference.Kind <> Imports)
      | ValueNone -> Array.empty

    let merged = Array.append syntaxSymbols lineSymbols
    let mergedReferences = Array.append syntaxReferences lineReferences
    cancellation.ThrowIfCancellationRequested()
    let struct (ordered, orderedReferences) = normalize merged mergedReferences
    let symbolsTruncated = ordered.Length > Limits.MaxSymbolsPerFile
    let retained = if symbolsTruncated then ordered[.. Limits.MaxSymbolsPerFile - 1] else ordered
    let numbered = assignOrdinals retained

    let retainedReferences =
      if symbolsTruncated then orderedReferences |> Array.filter (fun reference -> reference.Source < numbered.Length)
      else orderedReferences

    let explains = explainsReferences language numbered cancellation

    let allReferences =
      if explains.Length = 0 then retainedReferences
      else
        let combined = Array.append retainedReferences explains
        Array.sortInPlaceWith compareReferences combined
        combined

    let referencesTruncated = allReferences.Length > Limits.MaxReferencesPerFile

    let truncated =
      symbolsTruncated
      || referencesTruncated
      || lineResult.Truncated
      || (match syntax with
          | ValueSome result -> result.Truncated
          | ValueNone -> false)

    cancellation.ThrowIfCancellationRequested()

    { Tier = tier
      Symbols = numbered
      References =
        if referencesTruncated then allReferences[.. Limits.MaxReferencesPerFile - 1]
        else allReferences
      FileFlags =
        generatedFlag
        ||| (if truncated then NodeFlags.ExtractionTruncated else NodeFlags.None)
      LineCount = ValueSome lineResult.LineCount
      Truncated = truncated
      Skipped = skipped }
