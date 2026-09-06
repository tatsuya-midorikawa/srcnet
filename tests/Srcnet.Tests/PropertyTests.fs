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

[<Fact>]
let ``参照候補の書き出しと読み取りは往復で一致する`` () =
  // backlog 016 の完了条件。固定長レコードと文字列 blob 参照の往復が、
  // 件数・種別・確度・根拠段階のいずれでも情報を落とさないことを確かめる。
  let edgeKinds =
    [| Includes; Imports; Calls; References; Inherits; Implements; TypedAs; Tests; GuardedBy; Explains |]

  let confidences = [| Extracted; Resolved; Ambiguous |]

  let referenceGen =
    gen {
      let! kindIndex = Gen.choose (0, edgeKinds.Length - 1)
      let! confidenceIndex = Gen.choose (0, confidences.Length - 1)
      let! target = Gen.choose (0, 32)
      let! qualifier = Gen.choose (0, 8)
      let! line = Gen.choose (1, 100_000)
      let! startByte = Gen.choose (0, 1_000_000)
      let! length = Gen.choose (0, 4_096)
      let! stage = Gen.choose (0, 6)

      // `Writer` には同名のフィールドを持つレコードが複数あるため、型を明示して選ぶ。
      let reference: Writer.ReferenceInput =
        { Source = -1
          Kind = edgeKinds[kindIndex]
          // 対象は生テキストである。CJK と記号を含めて、文字列表の往復も同時に確かめる。
          Target = $"対象_{target}::name"
          Qualifier = (if qualifier = 0 then "" else $"scope{qualifier}")
          Language = C
          StartByte = startByte
          EndByte = startByte + length
          Line = line
          Stage = byte stage
          Confidence = confidences[confidenceIndex] }

      return reference
    }

  check (
    Prop.forAll (Arb.fromGen (Gen.listOf referenceGen |> Gen.map List.toArray)) (fun references ->
      let directory =
        IO.Path.Combine(IO.Path.GetTempPath(), "srcnet-refs-" + Guid.NewGuid().ToString "N")

      IO.Directory.CreateDirectory directory |> ignore

      try
        let repository =
          match RepositoryId.tryCreate "roundtrip" with
          | Ok value -> value
          | Error error -> failwith (RepositoryId.describe error)

        let path =
          match tryCreate "a.c" with
          | Ok value -> value
          | Error error -> failwith (PathError.describe error)

        let input: Writer.IndexInput =
          { Repository = repository
            Directories = Array.empty
            Files =
              [| ({ Path = path
                    SizeBytes = 0L
                    Language = C
                    EncodingCode = Encodings.toCode Encodings.Utf8
                    Flags = NodeFlags.None
                    LineCount = 0
                    ContentHash = Array.zeroCreate Hashing.HashLength
                    Symbols = Array.empty
                    References = references }
                 : Writer.FileInput) |] }

        let diagnostics = Diagnostics.DiagnosticSink()
        let written = Writer.write directory input diagnostics Threading.CancellationToken.None
        Assert.Equal(references.Length, written.ReferenceCount)

        let descriptor =
          written.Segments
          |> Array.find (fun segment -> segment.Name.EndsWith(".refs", StringComparison.Ordinal))

        use segment =
          match Reader.MappedSegment.Open(IO.Path.Combine(directory, descriptor.Name)) with
          | Ok value -> value
          | Error error -> failwith (Reader.OpenError.describe error)

        let payload = segment.Payload
        Assert.Equal(int64 references.Length, int64 segment.Header.PrimaryCount)
        let mutable ok = true

        for index in 0 .. references.Length - 1 do
          let expected = references[index]
          let record = payload.Slice(index * Format.RecordLength, Format.RecordLength)

          // span は closure へ渡せないため、読み出しはその場で行う。
          let line = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.LineOffset, 4))
          let source = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.SourceOffset, 4))
          let target = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.TargetOffset, 4))
          let qualifierRef = Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.ReferenceRecord.QualifierOffset, 4))
          let startByte = Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.ReferenceRecord.StartByteOffset, 8))
          let endByte = Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.ReferenceRecord.EndByteOffset, 8))

          if record[Format.ReferenceRecord.EdgeKindOffset] <> EdgeKind.toCode expected.Kind then ok <- false
          if record[Format.ReferenceRecord.StageOffset] <> expected.Stage then ok <- false

          if record[Format.ReferenceRecord.ConfidenceOffset] <> Confidence.toCode expected.Confidence then
            ok <- false

          if line <> uint32 expected.Line then ok <- false
          if startByte <> int64 expected.StartByte then ok <- false
          if endByte <> int64 expected.EndByte then ok <- false

          // 発生元はファイル ノード（密インデックス 1）へ写る。
          if source <> 1u then ok <- false

          // 文字列参照は表の範囲に収まる。
          if target >= uint32 written.StringCount then ok <- false
          if qualifierRef >= uint32 written.StringCount then ok <- false

        ok
      finally
        if IO.Directory.Exists directory then IO.Directory.Delete(directory, true))
  )
