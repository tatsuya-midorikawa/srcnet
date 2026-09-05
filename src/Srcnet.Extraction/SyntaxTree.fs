/// tree-sitter の構文木に対する安全な managed ラッパー。
///
/// 相互運用の境界では、寿命・範囲・null を必ず検証する（docs/security.md 4）。
/// 具体的には次の 3 つを型と実行時検査で保証する。
///
/// 1. ノードは所属する木より長く生存しない（木の破棄後の使用は例外にする）
/// 2. ノードのバイト範囲は必ず元のソース長に収まる
/// 3. 木の解放は破棄で確実に行われ、二重解放しない
module Srcnet.Extraction.SyntaxTree

open System

/// 木の走査で許容する最大の深さ。
/// 極端に深い入れ子を持つ入力で stack を使い切らないための上限である。
/// docs/security.md C-4 を参照。
[<Literal>]
let MaxWalkDepth = 512

/// 構文木。破棄すると配下のノードは使用できなくなる。
[<Sealed>]
type Tree internal (handle: nativeint, sourceLength: int) =
  let mutable disposed = false

  member internal _.Handle =
    ObjectDisposedException.ThrowIf(disposed, typeof<Tree>)
    handle

  member internal _.SourceLength = sourceLength

  member _.IsDisposed = disposed

  interface IDisposable with

    member _.Dispose() =
      if not disposed then
        disposed <- true
        Native.ts_tree_delete handle

/// 構文ノード。所属する木への参照を持ち、木より長く生存できない。
/// 同一性は木とノード識別子で決まり、順序は意味を持たないため比較を許さない。
[<Struct; NoComparison; NoEquality>]
type Node =
  internal
    { Owner: Tree
      Handle: Native.TSNode }

  member private this.Checked =
    ObjectDisposedException.ThrowIf(this.Owner.IsDisposed, typeof<Tree>)
    this.Handle

  /// 文法が定める種別名（`function_definition` など）。文法が所有する静的文字列。
  member this.Kind = Native.readStaticString(Native.ts_node_type this.Checked)

  /// 文法上意味を持つノードか。区切り記号などの匿名ノードと区別する。
  member this.IsNamed = Native.toBool(Native.ts_node_is_named this.Checked)

  /// 部分木に構文誤りを含むか。誤りを含む木でも解析は継続する。
  member this.HasError = Native.toBool(Native.ts_node_has_error this.Checked)

  /// ソース内のバイト範囲。必ず `[0, sourceLength]` に収める。
  ///
  /// tree-sitter が範囲外を返すことは想定していないが、未検証の値を
  /// そのままオフセットとして使わないという原則をここでも守る。
  member this.StartByte =
    min (int (Native.ts_node_start_byte this.Checked)) this.Owner.SourceLength

  member this.EndByte =
    let value = min (int (Native.ts_node_end_byte this.Checked)) this.Owner.SourceLength
    max value this.StartByte

  /// 1 起点の行番号。tree-sitter は 0 起点で返す。
  member this.StartLine = int (Native.ts_node_start_point this.Checked).Row + 1

  member this.EndLine = int (Native.ts_node_end_point this.Checked).Row + 1

  member this.ChildCount = int (Native.ts_node_child_count this.Checked)

  member this.NamedChildCount = int (Native.ts_node_named_child_count this.Checked)

  /// `index` 番目の子。範囲外は `ValueNone`。
  member this.Child(index: int) =
    if index < 0 || index >= this.ChildCount then ValueNone
    else
      let child = Native.ts_node_child(this.Checked, uint32 index)

      if Native.toBool(Native.ts_node_is_null child) then ValueNone
      else ValueSome { Owner = this.Owner; Handle = child }

  /// `index` 番目の名前付き子。範囲外は `ValueNone`。
  member this.NamedChild(index: int) =
    if index < 0 || index >= this.NamedChildCount then ValueNone
    else
      let child = Native.ts_node_named_child(this.Checked, uint32 index)

      if Native.toBool(Native.ts_node_is_null child) then ValueNone
      else ValueSome { Owner = this.Owner; Handle = child }

  /// 文法が定める役割名で子を取り出す（`name`、`declarator` など）。
  member this.ChildByField(field: string) =
    let bytes = Text.Encoding.UTF8.GetBytes field
    let pinned = Runtime.InteropServices.GCHandle.Alloc(bytes, Runtime.InteropServices.GCHandleType.Pinned)

    try
      let child =
        Native.ts_node_child_by_field_name(this.Checked, pinned.AddrOfPinnedObject(), uint32 bytes.Length)

      if Native.toBool(Native.ts_node_is_null child) then ValueNone
      else ValueSome { Owner = this.Owner; Handle = child }
    finally
      pinned.Free()

  /// ソースからこのノードに対応するバイト列を切り出す。
  /// 範囲は検証済みなので、呼び出し側で再度の境界検査は要らない。
  member this.Slice(source: ReadOnlySpan<byte>) =
    let start = min this.StartByte source.Length
    let finish = min this.EndByte source.Length
    source.Slice(start, max 0 (finish - start))

  /// ソースからこのノードに対応する文字列を取り出す。
  member this.Text(source: byte[]) =
    let start = min this.StartByte source.Length
    let finish = min this.EndByte source.Length
    Text.Encoding.UTF8.GetString(source, start, max 0 (finish - start))

let root (tree: Tree) =
  { Owner = tree
    Handle = Native.ts_tree_root_node tree.Handle }

/// 名前付きノードを深さ優先で列挙する。深さは `MaxWalkDepth` で打ち切る。
///
/// 再帰ではなく明示的なスタックで走査するため、深い入れ子でも stack overflow しない。
let descendants (start: Node) =
  seq {
    let pending = Collections.Generic.Stack<struct (Node * int)>()
    pending.Push(struct (start, 0))

    while pending.Count > 0 do
      let struct (node, depth) = pending.Pop()
      yield node

      if depth < MaxWalkDepth then
        // 逆順に積むことで、取り出す順序が原文の出現順に一致する。
        for index in node.NamedChildCount - 1 .. -1 .. 0 do
          match node.NamedChild index with
          | ValueSome child -> pending.Push(struct (child, depth + 1))
          | ValueNone -> ()
  }
