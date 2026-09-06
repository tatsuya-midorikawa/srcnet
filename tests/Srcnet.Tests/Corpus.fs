/// テスト用の固定コーパスの位置決めと読み取り。
///
/// コーパスは本リポジトリ内に持ち、外部依存にしない（docs/testing.md 5）。
/// テスト アセンブリの位置からリポジトリのルートを遡って探す。作業ディレクトリに
/// 依存させると、実行方法によって結果が変わる。
module Srcnet.Tests.Corpus

open System
open System.IO

/// コーパスと期待値を置くディレクトリ。リポジトリ ルートからの相対。
[<Literal>]
let CorpusDirectory = "tests/corpus"

[<Literal>]
let GoldenDirectory = "tests/golden"

/// リポジトリのルート。見つからない場合は例外にする。
/// テストの前提が崩れている状態を、静かに空のコーパスとして通してはならない。
let root =
  lazy
    (let mutable current = DirectoryInfo AppContext.BaseDirectory
     let mutable found = ValueNone

     while found.IsNone && not (isNull (box current)) do
       if Directory.Exists(Path.Combine(current.FullName, CorpusDirectory)) then
         found <- ValueSome current.FullName
       else
         current <- current.Parent

     match found with
     | ValueSome path -> path
     | ValueNone -> failwith $"{CorpusDirectory} を含むリポジトリ ルートが見つかりません")

let corpusPath (name: string) =
  Path.Combine(root.Value, CorpusDirectory, name)

let goldenPath (name: string) =
  Path.Combine(root.Value, GoldenDirectory, name)

/// コーパス内のファイルを、リポジトリ相対の論理パス昇順で列挙する。
///
/// 並びを序数順に固定するのは、ファイルシステムの列挙順に期待値が依存しないようにするため。
let files (name: string) =
  let directory = corpusPath name

  if not (Directory.Exists directory) then Array.empty
  else
    Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
    |> Seq.map (fun path -> struct (Path.GetRelativePath(directory, path).Replace('\\', '/'), path))
    |> Seq.sortWith (fun (struct (left, _)) (struct (right, _)) -> String.CompareOrdinal(left, right))
    |> Seq.toArray

/// 巨大な病的入力を用意する。
///
/// 内容は決定的な生成規則で決まるため、リポジトリへ載せずに実行時へ作る。数 MiB の
/// 生成物をリポジトリへ置くと、履歴の大きさに見合う情報がない。生成規則そのものが
/// リポジトリ内にあるので、外部依存にはならない（docs/testing.md 5）。
let materializeLarge () =
  let directory = Path.Combine(Path.GetTempPath(), "srcnet-corpus-large")
  Directory.CreateDirectory directory |> ignore

  let write (name: string) (build: unit -> byte[]) =
    let path = Path.Combine(directory, name)
    if not (File.Exists path) then File.WriteAllBytes(path, build ())
    path

  [| // 極端に長い 1 行。行長の上限と打ち切りを働かせる。
     write "long_line.c" (fun () ->
       let builder = Text.StringBuilder()
       builder.Append "int x = " |> ignore

       for _ in 1..200_000 do
         builder.Append "1 + " |> ignore

       builder.Append "1;\n" |> ignore
       Text.Encoding.UTF8.GetBytes(builder.ToString()))
     // 極端に多い行。行数の上限と、子が非常に多い構文ノードの走査を働かせる。
     write "many_lines.c" (fun () ->
       Text.Encoding.UTF8.GetBytes(String.replicate 200_000 "// line\n")) |]

/// 期待値の更新を要求されているか。
///
/// 期待値は手作業で書き換えず、生成コマンドで更新する（backlog 025）。
///
///     SRCNET_UPDATE_GOLDEN=1 dotnet test tests/Srcnet.Tests --filter Golden
let updateRequested =
  match Environment.GetEnvironmentVariable "SRCNET_UPDATE_GOLDEN" with
  | null -> false
  | "" -> false
  | "0" -> false
  | _ -> true

/// 期待値を突き合わせる。更新が要求されていれば書き出す。
///
/// 期待値は LF・BOM なし UTF-8 で保存する。改行の差が全件の差分になる状況を作らない（ADR-7）。
let compare (name: string) (actual: string) =
  let path = goldenPath name

  if updateRequested then
    Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".")
    |> ignore

    use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
    let bytes = Text.Encoding.UTF8.GetBytes actual
    stream.Write(ReadOnlySpan bytes)
    struct (true, "")
  elif not (File.Exists path) then
    struct (false, $"期待値 {name} がありません。SRCNET_UPDATE_GOLDEN=1 で生成してください")
  else
    // 改行を正規化してから比較する。チェックアウト時の改行変換で全件が差分にならないようにする。
    let expected = File.ReadAllText(path).Replace("\r\n", "\n")

    if String.Equals(expected, actual, StringComparison.Ordinal) then struct (true, "")
    else
      let expectedLines = expected.Split '\n'
      let actualLines = actual.Split '\n'
      let mutable index = 0
      let mutable difference = ""

      while difference = "" && index < max expectedLines.Length actualLines.Length do
        let left = if index < expectedLines.Length then expectedLines[index] else "(なし)"
        let right = if index < actualLines.Length then actualLines[index] else "(なし)"

        if not (String.Equals(left, right, StringComparison.Ordinal)) then
          difference <- $"{name} の {index + 1} 行目が違います\n  期待: {left}\n  実際: {right}"

        index <- index + 1

      struct (false, difference)
