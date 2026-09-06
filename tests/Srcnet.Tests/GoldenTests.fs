/// 抽出規則のゴールデン テスト。
///
/// 抽出規則の回帰を検出する唯一の手段であり、規則の変更が意図した差分だけを生むことを
/// 保証する（docs/roadmap.md M2、backlog 025）。
///
/// 期待値は決定的なテキスト表現で保存する。バイナリ成果物を期待値にしない。バイト位置は
/// 些細な編集で全件が動くため、行範囲を主とし、位置そのものは含めない。
///
/// 期待値の更新:
///
///     SRCNET_UPDATE_GOLDEN=1 dotnet test tests/Srcnet.Tests --filter Golden
module Srcnet.Tests.GoldenTests

open System
open System.Text
open System.Threading
open Xunit
open Xunit.Abstractions
open Srcnet.Core.Graph
open Srcnet.Discovery
open Srcnet.Extraction
open Srcnet.Text

/// 解析器が無い環境でテストを通したことを、結果から判別できるようにするための印。
[<Literal>]
let SkipMarker = "SKIPPED"

/// CI の `parser` ジョブは文法を構築したうえで実行する。そこで解析器が無いのは
/// 構成の誤りであり、「解析器が無いから通った」状態を成功として扱ってはならない。
let private parserRequired =
  match Environment.GetEnvironmentVariable "SRCNET_REQUIRE_PARSER" with
  | null
  | ""
  | "0" -> false
  | _ -> true

/// フラグを安定した順序の名前列にする。列挙の反復順に依存させない。
let private flagNames (flags: NodeFlags) =
  let all =
    [| NodeFlags.Definition, "definition"
       NodeFlags.DeclarationOnly, "declaration"
       NodeFlags.Test, "test"
       NodeFlags.Generated, "generated"
       NodeFlags.Vendored, "vendored"
       NodeFlags.Conditional, "conditional"
       NodeFlags.Binary, "binary"
       NodeFlags.UndeterminedEncoding, "undetermined-encoding"
       NodeFlags.Skipped, "skipped"
       NodeFlags.SymbolicLink, "symlink"
       NodeFlags.AmbiguousEncoding, "ambiguous-encoding"
       NodeFlags.InternalLinkage, "internal-linkage"
       NodeFlags.ExtractionTruncated, "extraction-truncated" |]

  let selected =
    all
    |> Array.filter (fun (flag, _) -> flags.HasFlag flag)
    |> Array.map snd

  if selected.Length = 0 then "-" else String.Join(",", selected)

/// 抽出結果を人間が読める決定的なテキストにする。
let private renderFile (builder: StringBuilder) (relative: string) (language: Language) (extracted: Model.ExtractedFile) =
  builder
    .Append("## ")
    .Append(relative)
    .Append(" lang=")
    .Append(Language.name language)
    .Append(" tier=")
    .Append(Model.Tier.name extracted.Tier)
  |> ignore

  if extracted.Truncated then builder.Append " truncated" |> ignore

  match extracted.Skipped with
  | ValueSome Model.NoGrammar -> builder.Append " skipped=no-grammar" |> ignore
  | ValueSome Model.NotText -> builder.Append " skipped=not-text" |> ignore
  | ValueSome Model.ParseTimedOut -> builder.Append " skipped=timed-out" |> ignore
  | ValueSome(Model.TooLargeToExtract _) -> builder.Append " skipped=too-large" |> ignore
  | ValueSome(Model.ParseUnavailable detail) -> builder.Append(" skipped=").Append(detail) |> ignore
  | ValueSome(Model.AmbiguousEncoding candidates) ->
    builder.Append(" skipped=ambiguous-encoding(").Append(candidates).Append(')') |> ignore
  | ValueSome(Model.UnsupportedEncoding encoding) ->
    builder.Append(" skipped=unsupported-encoding(").Append(encoding).Append(')') |> ignore
  | ValueNone -> ()

  builder.Append '\n' |> ignore

  for symbol in extracted.Symbols do
    let parent =
      if symbol.Parent < 0 || symbol.Parent >= extracted.Symbols.Length then "-"
      else extracted.Symbols[symbol.Parent].QualifiedName

    builder
      .Append("N ")
      .Append(NodeKind.name symbol.Kind)
      .Append(' ')
      .Append(symbol.Name)
      .Append(" | ")
      .Append(symbol.QualifiedName)
      .Append(" ord=")
      .Append(symbol.Ordinal)
      .Append(" lines=")
      .Append(symbol.StartLine)
      .Append('-')
      .Append(symbol.EndLine)
      .Append(" parent=")
      .Append(parent)
      .Append(" flags=")
      .Append(flagNames symbol.Flags)
    |> ignore

    if symbol.Condition <> "" then builder.Append(" cond=").Append(symbol.Condition) |> ignore

    // `docRef` は原文の範囲を指す。要約は生成しない（docs/graph-model.md 5）。
    let doc = symbol.Doc

    if not doc.IsEmpty then
      builder.Append(" doc=").Append(doc.End - doc.Start).Append("B") |> ignore

    builder.Append '\n' |> ignore

  for reference in extracted.References do
    let source =
      if reference.Source < 0 || reference.Source >= extracted.Symbols.Length then "(file)"
      else extracted.Symbols[reference.Source].QualifiedName

    builder
      .Append("E ")
      .Append(EdgeKind.name reference.Kind)
      .Append(' ')
      .Append(source)
      .Append(" -> ")
      .Append(reference.Target)
      .Append(" conf=")
      .Append(Confidence.name reference.Confidence)
      .Append(" stage=")
      .Append(reference.Stage)
      .Append(" line=")
      .Append(reference.Line)
    |> ignore

    if reference.Qualifier <> "" then builder.Append(" in=").Append(reference.Qualifier) |> ignore

    builder.Append '\n' |> ignore

/// コーパス全体を抽出し、期待値のテキストを作る。
///
/// 走査と同じ経路（読み取り → 復号 → 抽出）を通す。抽出器を直接叩くと、
/// 復号と属性の伝播が期待値に反映されない。
let private renderCorpus (name: string) (tier: Model.Tier) =
  let builder = StringBuilder()

  builder
    .Append("# corpus=")
    .Append(name)
    .Append(" tier=")
    .Append(Model.Tier.name tier)
    .Append('\n')
  |> ignore

  let options =
    { Extractor.ExtractionOptions.defaults with
        Tier = tier }

  let extractor = Extractor.Extractor options
  use reader = new Content.ContentReader(Walk.WalkOptions.DefaultMaxFileSizeBytes, options.MaxExtractionBytes)

  for struct (relative, full) in Corpus.files name do
    let logical =
      match Srcnet.Core.Paths.tryCreate relative with
      | Ok path -> Srcnet.Core.Paths.value path
      | Error _ -> relative

    let language = Classify.language (Srcnet.Core.Paths.tryCreate relative |> Result.defaultValue Srcnet.Core.Paths.root)
    let info = IO.FileInfo full

    match reader.Read(full, info.Length, CancellationToken.None) with
    | Error error -> builder.Append("## ").Append(relative).Append(" read-error=").Append(Content.ReadError.describe error).Append('\n') |> ignore
    | Ok summary ->
      let flags = Classify.pathFlags (Srcnet.Core.Paths.tryCreate relative |> Result.defaultValue Srcnet.Core.Paths.root)

      let summaryFlags = summary.Flags

      let extracted =
        if summary.Decoded && summary.DecodedLength > 0 then
          extractor.Extract(language, logical, reader.Decoded, summary.DecodedLength, flags ||| summary.Flags, CancellationToken.None)
        elif summary.Decoded then Model.ExtractedFile.empty tier
        elif summaryFlags.HasFlag NodeFlags.Binary then
          Model.ExtractedFile.skipped Model.Structure Model.NotText
        elif summary.EncodingCandidates.Length > 1 then
          // 走査と同じ扱いにする。候補の先頭を確定値として扱わない（backlog 022）。
          let names = summary.EncodingCandidates |> Array.map Encodings.name |> String.concat ", "
          Model.ExtractedFile.skipped Model.Structure (Model.AmbiguousEncoding names)
        else
          Model.ExtractedFile.skipped
            Model.Structure
            (Model.UnsupportedEncoding(Encodings.name summary.Encoding))

      renderFile builder relative language extracted

  builder.ToString()

/// 解析器が無い環境では T2 の期待値を検証しない。
///
/// 「解析器が無いから通った」状態を隠さないため、二つの手当てをする。
/// 1. 省略したことをテスト出力へ明示する
/// 2. `SRCNET_REQUIRE_PARSER=1` の環境では、省略そのものを失敗にする
let private skipWithoutParser (output: ITestOutputHelper) =
  if Extractor.supportsSyntax Language.C then false
  else
    if parserRequired then
      failwith "SRCNET_REQUIRE_PARSER=1 ですが、C / C++ の文法を同梱していません"

    output.WriteLine $"{SkipMarker}: C / C++ の文法が同梱されていないため、T2 のゴールデンは検証しません"
    true

let private check (corpus: string) (tier: Model.Tier) =
  let actual = renderCorpus corpus tier
  let name = $"{corpus}.{(Model.Tier.name tier).ToLowerInvariant()}.txt"
  let struct (ok, detail) = Corpus.compare name actual

  if not ok then failwith detail

// --- T1 のみ。解析器の有無にかかわらず結果が変わってはならない ---

[<Fact>]
let ``Golden: micro コーパスの T1 抽出`` () = check "micro" Model.LineOriented

[<Fact>]
let ``Golden: cjk コーパスの T1 抽出`` () = check "cjk" Model.LineOriented

// --- T2。解析器がある環境でのみ検証する ---

type GoldenSyntaxTests(output: ITestOutputHelper) =

  [<Fact>]
  member _.``Golden: micro コーパスの T2 抽出``() =
    if not (skipWithoutParser output) then check "micro" Model.Syntax

  [<Fact>]
  member _.``Golden: cjk コーパスの T2 抽出``() =
    if not (skipWithoutParser output) then check "cjk" Model.Syntax

  [<Fact>]
  member _.``抽出は二度実行しても同じ結果を返す``() =
    // ノード ID の安定性の前提になる。同じ入力から違う結果が出れば決定性が壊れている。
    let first = renderCorpus "micro" Model.Syntax
    let second = renderCorpus "micro" Model.Syntax
    Assert.Equal(first, second)

  [<Fact>]
  member _.``CJK の識別子が NFC 正規化されて欠落なく抽出される``() =
    if not (skipWithoutParser output) then
      let rendered = renderCorpus "cjk" Model.Syntax

      // 文字化けの検出。置換文字が現れたら復号か走査が壊れている。
      Assert.DoesNotContain("\uFFFD", rendered)
      Assert.Contains("面積", rendered)
      Assert.Contains("座標", rendered)
      Assert.Contains("넓이", rendered)
      Assert.Contains("方塊", rendered)

  [<Fact>]
  member _.``属性がパスと内容からシンボルへ伝播する``() =
    // backlog 023 の完了条件。ゴールデンの期待値に含まれることを、
    // 差分を読まなくても分かる形で固定する。
    if not (skipWithoutParser output) then
      let rendered = renderCorpus "micro" Model.Syntax
      let lines = rendered.Split '\n'

      let symbolLine (name: string) =
        lines
        |> Array.tryFind (fun line -> line.StartsWith("N ", StringComparison.Ordinal) && line.Contains $" {name} | ")

      let flagsOf name =
        match symbolLine name with
        | Some line -> line
        | None -> failwith $"{name} が抽出されていません"

      // `third_party/` 配下は除外せず、フラグ付きで保持する。
      Assert.Contains("vendored", flagsOf "vendor_open")

      // Google Test 系のマクロで定義された関数はテストとして扱う。
      Assert.Contains("test", flagsOf "ComputesArea")
      Assert.Contains("test", flagsOf "HandlesZero")

      // 先頭にマーカーを持つファイルは生成コード、本文にのみ含むファイルは違う。
      Assert.Contains("generated", flagsOf "generated_value")
      Assert.DoesNotContain("generated", flagsOf "handwritten_value")
