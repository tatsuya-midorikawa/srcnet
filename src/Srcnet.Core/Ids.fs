/// ノード ID の計算と表現。
///
/// ID は決定的で、無関係な編集で変わらず、増分更新をまたいで安定し、10^8 規模で衝突しない
/// ことを要件とする。バイト位置や行番号を含めないのはこの要件のためである。
/// docs/graph-model.md 4 を参照。
module Srcnet.Core.Ids

open System
open System.Buffers
open System.Buffers.Binary
open System.Text
open Srcnet.Text
open Srcnet.Core.Graph
open Srcnet.Core.Paths

/// ID 計算方式の版。材料の並びや区切り方、ハッシュ関数を変えるときに増やす。
/// 版が変われば同じ入力から別の ID が出るため、成果物の非互換変更になる。
///
/// 2: ハッシュを自前 BLAKE3 から BCL の SHA-256 へ変更（ADR-8 の見直し）。
[<Literal>]
let SchemeVersion = 2uy

/// ノード ID のバイト長。128 ビットあれば 10^9 ノードでも衝突確率は無視できる。
[<Literal>]
let NodeIdLength = 16

/// 128 ビットのノード ID。
///
/// 上位・下位ともビッグ エンディアンで解釈するため、数値としての比較順と
/// バイト列としての辞書順が一致する。`.idmap` の二分探索はこの性質に依存する。
[<Struct; StructuralEquality; StructuralComparison>]
type NodeId =
  { High: uint64
    Low: uint64 }

  override this.ToString() =
    String.Create(
      32,
      this,
      fun destination id ->
        let mutable buffer = Span<byte>(Array.zeroCreate NodeIdLength)
        BinaryPrimitives.WriteUInt64BigEndian(buffer, id.High)
        BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice 8, id.Low)

        for i in 0 .. NodeIdLength - 1 do
          let b = int buffer[i]
          destination[i * 2] <- "0123456789abcdef"[b >>> 4]
          destination[i * 2 + 1] <- "0123456789abcdef"[b &&& 0xF]
    )

module NodeId =

  /// 16 進小文字 32 桁の表記。
  ///
  /// 構造体のメンバーを読み取り専用の位置から直接呼ぶと複製の警告が出るため、
  /// 値を受け取る関数として用意する。
  let toString (id: NodeId) = id.ToString()

  let ofBytes (bytes: ReadOnlySpan<byte>) =
    if bytes.Length < NodeIdLength then
      invalidArg (nameof bytes) "ノード ID には 16 バイト必要です"

    { High = BinaryPrimitives.ReadUInt64BigEndian bytes
      Low = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice 8) }

  let writeTo (destination: Span<byte>) (id: NodeId) =
    BinaryPrimitives.WriteUInt64BigEndian(destination, id.High)
    BinaryPrimitives.WriteUInt64BigEndian(destination.Slice 8, id.Low)

  let private hexValue (c: char) =
    if c >= '0' && c <= '9' then ValueSome(int c - int '0')
    elif c >= 'a' && c <= 'f' then ValueSome(int c - int 'a' + 10)
    elif c >= 'A' && c <= 'F' then ValueSome(int c - int 'A' + 10)
    else ValueNone

  let tryParse (text: string) =
    if text.Length <> 32 then ValueNone
    else
      let mutable high = 0UL
      let mutable low = 0UL
      let mutable ok = true

      for i in 0..31 do
        match hexValue text[i] with
        | ValueNone -> ok <- false
        | ValueSome digit ->
          if i < 16 then high <- (high <<< 4) ||| uint64 digit
          else low <- (low <<< 4) ||| uint64 digit

      if ok then ValueSome { High = high; Low = low } else ValueNone

/// 解析ルートの論理名。絶対パスを含めないことで、成果物が実行環境に依存しなくなる。
[<Struct; StructuralEquality; StructuralComparison>]
type RepositoryId =
  private
  | RepositoryId of string

  member this.Value =
    let (RepositoryId value) = this
    value

  override this.ToString() = this.Value

module RepositoryId =

  type Error =
    | Empty
    | ContainsSeparator
    | ContainsControlCharacter
    | TooLong

  /// 生成物のファイル名には使わないが、識別子として扱いやすい長さに制限する。
  [<Literal>]
  let MaxLength = 128

  let describe error =
    match error with
    | Empty -> "リポジトリ ID が空です"
    | ContainsSeparator -> "リポジトリ ID にパス区切りを含められません"
    | ContainsControlCharacter -> "リポジトリ ID に制御文字を含められません"
    | TooLong -> "リポジトリ ID が長すぎます"

  let tryCreate (raw: string) =
    let normalized = Unicode.normalize raw

    if normalized.Length = 0 then Error Empty
    elif normalized.Length > MaxLength then Error TooLong
    elif normalized.IndexOf('/') >= 0 || normalized.IndexOf('\\') >= 0 then Error ContainsSeparator
    else
      let mutable hasControl = false

      for c in normalized do
        if c < ' ' || c = '\u007F' then hasControl <- true

      if hasControl then Error ContainsControlCharacter else Ok(RepositoryId normalized)

  let value (id: RepositoryId) = id.Value

/// ノード ID を計算する。
///
/// 材料は `方式版 ‖ 種別 ‖ repoId ‖ 論理パス ‖ 修飾名 ‖ 序数` で、各文字列は
/// 長さ前置きにより連結の曖昧さを排除する。長さを前置きしなければ、
/// `("ab", "c")` と `("a", "bc")` が同じ ID になってしまう。
///
/// 材料をいったん 1 つのバッファへ組み立ててから一度だけハッシュするのは、
/// ノード 1 件ごとに計算する熱い経路で、逐次 API の相互運用往復を 1 回に抑えるためである。
[<Sealed>]
type NodeIdBuilder() =
  let mutable buffer = ArrayPool<byte>.Shared.Rent 512
  let mutable length = 0
  let mutable disposed = false

  let ensureCapacity (required: int) =
    if buffer.Length < required then
      let grown = ArrayPool<byte>.Shared.Rent required
      Array.blit buffer 0 grown 0 length
      ArrayPool<byte>.Shared.Return buffer
      buffer <- grown

  let appendByte (value: byte) =
    ensureCapacity (length + 1)
    buffer[length] <- value
    length <- length + 1

  let appendUInt32 (value: uint32) =
    ensureCapacity (length + 4)
    BinaryPrimitives.WriteUInt32LittleEndian(Span(buffer, length, 4), value)
    length <- length + 4

  let appendLengthPrefixed (text: string) =
    let maxBytes = Encoding.UTF8.GetMaxByteCount text.Length
    ensureCapacity (length + 4 + maxBytes)
    let written = Encoding.UTF8.GetBytes(text.AsSpan(), Span(buffer, length + 4, buffer.Length - length - 4))
    BinaryPrimitives.WriteUInt32LittleEndian(Span(buffer, length, 4), uint32 written)
    length <- length + 4 + written

  member _.Compute(kind: NodeKind, repository: RepositoryId, path: LogicalPath, qualifiedName: string, ordinal: uint32) =
    ObjectDisposedException.ThrowIf(disposed, typeof<NodeIdBuilder>)
    length <- 0
    appendByte SchemeVersion
    appendByte (NodeKind.toCode kind)
    appendLengthPrefixed repository.Value
    appendLengthPrefixed path.Value
    appendLengthPrefixed qualifiedName
    appendUInt32 ordinal

    let mutable digest = Span<byte>(Array.zeroCreate Hashing.HashLength)
    Hashing.hashInto (ReadOnlySpan(buffer, 0, length)) digest
    NodeId.ofBytes (Span.op_Implicit digest)

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true
        ArrayPool<byte>.Shared.Return buffer
        buffer <- Array.Empty()
