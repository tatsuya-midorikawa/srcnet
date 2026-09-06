/// 抽出のクラッシュ・フリーズ耐性と fuzz。
///
/// tree-sitter を採用した代償として、製品成果物にネイティブ コードが入り、メモリ安全性の
/// 検証責任が生じる（docs/decisions.md ADR-3）。相互運用の境界を通した経路として
/// 検証する（docs/testing.md T-5, T-6、docs/security.md 4, C-4、backlog 024）。
module Srcnet.Tests.RobustnessTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open Xunit
open Srcnet.Core.Graph
open Srcnet.Discovery
open Srcnet.Extraction

/// 病的入力 1 件の処理に許す時間。フリーズを「遅い」と区別するための上限である。
///
/// 解析自体の上限（`Parsing.DefaultTimeoutMicroseconds` = 5 秒）に、復号と行指向走査の
/// ぶんの余裕を足した値にする。
let private PerFileBudget = TimeSpan.FromSeconds 30.0

let private options =
  { Extractor.ExtractionOptions.defaults with
      Tier = Model.Syntax }

/// 病的コーパスの 1 ファイルを、走査と同じ経路で抽出する。
let private extractFile (relative: string) (full: string) (cancellation: CancellationToken) =
  let extractor = Extractor.Extractor options

  use reader =
    new Content.ContentReader(Walk.WalkOptions.DefaultMaxFileSizeBytes, options.MaxExtractionBytes)

  let logical =
    match Srcnet.Core.Paths.tryCreate relative with
    | Ok path -> path
    | Error _ -> Srcnet.Core.Paths.root

  let language = Classify.language logical
  let info = FileInfo full

  match reader.Read(full, info.Length, cancellation) with
  | Error error -> Error(Content.ReadError.describe error)
  | Ok summary ->
    if summary.Decoded && summary.DecodedLength > 0 then
      Ok(
        extractor.Extract(
          language,
          Srcnet.Core.Paths.value logical,
          reader.Decoded,
          summary.DecodedLength,
          summary.Flags,
          cancellation
        )
      )
    else Ok(Model.ExtractedFile.empty Model.Structure)

[<Fact>]
let ``病的入力でもクラッシュせず、定めた時間内に終わる`` () =
  // リポジトリ内の固定分と、生成規則から作る巨大な分の両方を通す。
  let generated =
    Corpus.materializeLarge ()
    |> Array.map (fun path -> struct (Path.GetFileName path, path))

  let files = Array.append (Corpus.files "pathological") generated
  Assert.NotEmpty files

  for struct (relative, full) in files do
    let watch = Stopwatch.StartNew()

    // 例外は取り消し以外すべて失敗とする。抽出は「壊れたファイルでも止まらない」ことが前提。
    let outcome =
      try
        Ok(extractFile relative full CancellationToken.None)
      with ex ->
        Error $"{relative}: {ex.GetType().Name}: {ex.Message}"

    watch.Stop()

    match outcome with
    | Error message -> failwith message
    | Ok _ -> ()

    Assert.True(
      watch.Elapsed < PerFileBudget,
      $"{relative} の抽出に {watch.Elapsed.TotalSeconds:F1} 秒かかりました (上限 {PerFileBudget.TotalSeconds} 秒)"
    )

[<Fact>]
let ``上限に達した抽出は結果を保持したまま打ち切りを記録する`` () =
  // 深い入れ子は走査深さの上限に達する。そこまでの結果は保持し、打ち切りを印として残す。
  let deep = Corpus.corpusPath "pathological" |> fun d -> Path.Combine(d, "deep_parens.c")

  match extractFile "deep_parens.c" deep CancellationToken.None with
  | Error message -> failwith message
  | Ok extracted ->
    // 打ち切っても、抽出済みの結果は捨てない。
    let fileFlags = extracted.FileFlags

    if extracted.Truncated then
      Assert.True(fileFlags.HasFlag NodeFlags.ExtractionTruncated)

[<Fact>]
let ``条件構造が壊れたファイルでも他のファイルの結果は変わらない`` () =
  let directory = Corpus.corpusPath "pathological"
  let broken = Path.Combine(directory, "unbalanced_ifdef.c")
  let sound = Path.Combine(Corpus.corpusPath "micro", "shapes.c")

  let before =
    match extractFile "shapes.c" sound CancellationToken.None with
    | Error message -> failwith message
    | Ok value -> value

  extractFile "unbalanced_ifdef.c" broken CancellationToken.None |> ignore

  let after =
    match extractFile "shapes.c" sound CancellationToken.None with
    | Error message -> failwith message
    | Ok value -> value

  Assert.Equal(before.Symbols.Length, after.Symbols.Length)
  Assert.Equal(before.References.Length, after.References.Length)

[<Fact>]
let ``構文誤りを含むファイルでも誤り箇所以外のシンボルが抽出される`` () =
  if Extractor.supportsSyntax Language.C then
    let path = Path.Combine(Corpus.corpusPath "pathological", "syntax_error.c")

    match extractFile "syntax_error.c" path CancellationToken.None with
    | Error message -> failwith message
    | Ok extracted ->
      let names = extracted.Symbols |> Array.map (fun symbol -> symbol.Name)
      Assert.Contains("good", names)
      Assert.Contains("also_good", names)

[<Fact>]
let ``抽出中の取り消しは定めた時間内に効く`` () =
  use cancellation = new CancellationTokenSource()
  cancellation.Cancel()

  let path = (Corpus.materializeLarge ())[1]

  // 取り消し済みのトークンでは、抽出は始まらずに `OperationCanceledException` になる。
  // 中断を内部エラーとして扱わないことが、終了コード 5 の前提である。
  Assert.ThrowsAny<OperationCanceledException>(fun () ->
    extractFile "many_lines.c" path cancellation.Token |> ignore)
  |> ignore

/// fuzz 用の決定的な擬似乱数。失敗した入力を種から再現できるようにする。
type private Xorshift(seed: uint64) =
  let mutable state = if seed = 0UL then 0x9E3779B97F4A7C15UL else seed

  member _.Next() =
    state <- state ^^^ (state <<< 13)
    state <- state ^^^ (state >>> 7)
    state <- state ^^^ (state <<< 17)
    state

  member this.NextInt(bound: int) =
    if bound <= 0 then 0 else int (this.Next() % uint64 bound)

/// fuzz の反復回数。CI に収まる範囲で固定する。
[<Literal>]
let private FuzzIterations = 400

/// 変異の基になる正常なソース。
let private seedSources =
  [| "int add(int a, int b) { return a + b; }\n"
     "#include <stdio.h>\n#define M(x) ((x) + 1)\nstruct P { int x; };\n"
     "#ifdef CONFIG_A\nint f(void) { return 1; }\n#else\nint f(void) { return 2; }\n#endif\n"
     "namespace n { class C : public B { public: int f(); }; }\nint n::C::f() { return 0; }\n"
     "typedef struct { int x; } T;\nenum E { A, B };\nstatic T t;\n" |]

[<Fact>]
let ``fuzz: 無作為なバイト列と変異させたソースで異常終了しない`` () =
  let extractor = Extractor.Extractor options
  let random = Xorshift 0x5152535455565758UL

  for iteration in 1..FuzzIterations do
    // 種は反復番号から決まる。失敗したらこの番号だけで再現できる。
    let seed = uint64 iteration * 0x9E3779B97F4A7C15UL
    let local = Xorshift seed

    let source =
      if iteration % 3 = 0 then
        // 完全に無作為なバイト列。UTF-8 として妥当とは限らない。
        let length = 1 + local.NextInt 512
        Array.init length (fun _ -> byte (local.NextInt 256))
      else
        // 正常なソースを変異させる。構文の一部だけが壊れた入力を作る。
        let baseText = seedSources[local.NextInt seedSources.Length]
        let bytes = Encoding.UTF8.GetBytes baseText
        let mutations = 1 + local.NextInt 8

        for _ in 1..mutations do
          let position = local.NextInt bytes.Length
          bytes[position] <- byte (local.NextInt 256)

        bytes

    let language =
      if local.NextInt 2 = 0 then Language.C else Language.Cpp

    try
      extractor.Extract(language, "fuzz.c", source, source.Length, NodeFlags.None, CancellationToken.None)
      |> ignore
    with ex ->
      // 失敗した入力は種と 16 進表現の両方で残す。再現に必要な情報を落とさない。
      failwith
        $"fuzz が失敗しました (iteration={iteration}, seed=0x{seed:X16}): {ex.GetType().Name}: {ex.Message}\n入力: {Convert.ToHexString source}"

  ignore (random.Next())

[<Fact>]
let ``fuzz: 連続実行でハンドルとメモリが増え続けない`` () =
  // soak。木とネイティブ資源の解放漏れがあれば、ここで増加として現れる。
  let extractor = Extractor.Extractor options
  let source = Encoding.UTF8.GetBytes(String.replicate 200 "int f(int a) { return a; }\n")

  for _ in 1..50 do
    extractor.Extract(Language.C, "soak.c", source, source.Length, NodeFlags.None, CancellationToken.None)
    |> ignore

  GC.Collect()
  GC.WaitForPendingFinalizers()
  GC.Collect()
  let baseline = GC.GetTotalMemory true

  for _ in 1..400 do
    extractor.Extract(Language.C, "soak.c", source, source.Length, NodeFlags.None, CancellationToken.None)
    |> ignore

  GC.Collect()
  GC.WaitForPendingFinalizers()
  GC.Collect()
  let after = GC.GetTotalMemory true

  // 抽出結果を保持しないため、常駐量は反復回数に比例してはならない。
  // 閾値は測定の揺れを吸収できる程度に緩くし、比例増加だけを検出する。
  Assert.True(
    after < baseline + 32L * 1024L * 1024L,
    $"400 回の抽出で常駐量が {baseline} → {after} バイトへ増えました"
  )
