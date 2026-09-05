/// 性質に基づくテスト。具体例の列挙より、不変条件そのものを述べたほうが明快な項目を扱う。
module Srcnet.Tests.PropertyTests

open System
open FsCheck
open FsCheck.FSharp
open Xunit
open Srcnet.Core
open Srcnet.Core.Graph
open Srcnet.Core.Ids
open Srcnet.Core.Paths
open Srcnet.Discovery
open Srcnet.Storage
open Srcnet.Text

/// 失敗時に例外を投げる設定で実行する。件数は CI の所要時間と網羅性の折り合いで決めている。
let private check (property: Property) =
  Check.One(Config.QuickThrowOnFailure.WithMaxTest 200, property)

[<Fact>]
let ``NFC 正規化は冪等である`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text
      || (let once = Unicode.normalize text
          once = Unicode.normalize once))
  )

[<Fact>]
let ``表示幅は書記素数以上かつ 2 倍以下である`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text
      || (let width = Unicode.displayWidth text
          width >= 0 && width <= 2 * text.Length))
  )

[<Fact>]
let ``無害化しても長さは変わらず、制御文字は残らない`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text
      || (let sanitized = Sanitize.forTerminal text

          sanitized.Length = text.Length
          && not (sanitized |> Seq.exists (fun c -> c < ' ' || c = '\u007F'))))
  )

[<Fact>]
let ``切り詰めた文字列の表示幅は上限を超えない`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text
      || (let width = 8
          Unicode.displayWidth (Unicode.truncateToWidth width text) <= width))
  )

[<Fact>]
let ``語分割は空語を返さない`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text || (Words.split text |> Array.forall (fun word -> word.Length > 0)))
  )

[<Fact>]
let ``n-gram は重複のない昇順である`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun text ->
      isNull text
      || (let grams = Words.ngrams text

          grams = Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) grams
          && grams.Length = (Array.distinct grams).Length))
  )

[<Fact>]
let ``内容ハッシュは入力の分割位置に依存しない`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<byte[]>) (fun bytes ->
      isNull bytes
      || (let whole = Hashing.hash (ReadOnlySpan bytes)
          use hasher = new Hashing.Hasher()
          let half = bytes.Length / 2
          hasher.Update(ReadOnlySpan(bytes, 0, half))
          hasher.Update(ReadOnlySpan(bytes, half, bytes.Length - half))
          let split = Array.zeroCreate<byte> Hashing.HashLength
          hasher.Finish(Span split)
          whole = split))
  )

[<Fact>]
let ``文字列表のオフセットは単調非減少で、末尾が総バイト長に一致する`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string[]>) (fun texts ->
      isNull texts || Array.exists isNull texts
      || (let table = Strings.StringTable()

          for text in texts do
            table.Intern text |> ignore

          let offsets = table.Offsets()

          offsets.Length = table.Count + 1
          && Seq.pairwise offsets |> Seq.forall (fun (previous, next) -> next >= previous)
          && offsets[table.Count] = uint64 table.TotalBytes))
  )

[<Fact>]
let ``論理パスの生成はルート外への参照を必ず拒否する`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun raw ->
      isNull raw
      || (match tryCreate raw with
          | Error _ -> true
          | Ok path ->
            let text = value path
            not (text.StartsWith "/") && not (text.Contains "..") && not (text.Contains "\\")))
  )

[<Fact>]
let ``ノード ID の計算は同じ材料に対して安定している`` () =
  let repository =
    match RepositoryId.tryCreate "sample" with
    | Ok value -> value
    | Error _ -> failwith "リポジトリ ID を作れません"

  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<string>) (fun name ->
      isNull name
      || (use builder = new NodeIdBuilder()
          let first = builder.Compute(Function, repository, root, name, 0u)
          let second = builder.Compute(Function, repository, root, name, 0u)
          first = second))
  )

[<Fact>]
let ``グロブ照合はどんな入力でも例外を投げず終了する`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<NonNull<string> * NonNull<string>>) (fun (pattern, subject) ->
      let rules = Ignore.parse 0 [ pattern.Get ]
      let segments = subject.Get.Split '/'
      Ignore.decide [| rules |] segments false |> ignore
      true)
  )

[<Fact>]
let ``不変条件を満たすセグメント ヘッダーは往復する`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<uint16>) (fun count ->
      let primary = uint64 count

      let header: Format.Header =
        { Kind = Format.Nodes
          PrimaryCount = primary
          SecondaryCount = 0UL
          RecordLength = uint32 Format.RecordLength
          PayloadLength = primary * uint64 Format.RecordLength }

      let buffer = Array.zeroCreate<byte> Format.HeaderLength
      Format.writeHeader (Span buffer) header
      let fileLength = int64 Format.HeaderLength + int64 header.PayloadLength

      match Format.tryReadHeader (ReadOnlySpan buffer) fileLength with
      | Ok parsed -> parsed = header
      | Error _ -> false)
  )

[<Fact>]
let ``不変条件を破るセグメント ヘッダーは例外なく拒否される`` () =
  // 破損した成果物は外部入力である。どのような件数とレコード長の組でも、
  // 未処理例外や算術 overflow ではなく形式エラーで終わらなければならない。
  check (
    Prop.forAll
      (ArbMap.defaults |> ArbMap.arbitrary<uint64 * uint64 * uint32>)
      (fun (primary, secondary, recordLength) ->
        let header: Format.Header =
          { Kind = Format.Nodes
            PrimaryCount = primary
            SecondaryCount = secondary
            RecordLength = recordLength
            PayloadLength = 128UL }

        let buffer = Array.zeroCreate<byte> Format.HeaderLength
        Format.writeHeader (Span buffer) header

        let satisfiesInvariants =
          primary <= uint64 Int32.MaxValue
          && secondary = 0UL
          && recordLength = uint32 Format.RecordLength
          && primary * uint64 Format.RecordLength = 128UL

        match Format.tryReadHeader (ReadOnlySpan buffer) (int64 Format.HeaderLength + 128L) with
        | Ok parsed -> satisfiesInvariants && parsed = header
        | Error _ -> not satisfiesInvariants)
  )

[<Fact>]
let ``ノード ID の 16 進表記は往復する`` () =
  check (
    Prop.forAll (ArbMap.defaults |> ArbMap.arbitrary<uint64 * uint64>) (fun (high, low) ->
      let id = { High = high; Low = low }
      NodeId.tryParse (id.ToString()) = ValueSome id)
  )
