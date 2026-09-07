/// tree-sitter C API への相互運用宣言。
///
/// 構文解析には tree-sitter を再利用する（docs/decisions.md ADR-3）。
/// このモジュールは生の境界だけを扱い、寿命と範囲の検証は `SyntaxTree` が担う。
///
/// 読み込む共有ライブラリは srcnet 自身が `tools/build_native.py` で構築したものだけで、
/// 解析対象リポジトリ由来の文法やライブラリを読み込むことは一切ない
/// （docs/security.md C-1）。
module internal Srcnet.Extraction.Native

open System
open System.Runtime.InteropServices

// Restrict .NET's native probing instead of searching the indexed working directory.
[<assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)>]
do ()

/// 構築される共有ライブラリの基底名。実際のファイル名は OS ごとに
/// `libsrcnet_treesitter.dylib` / `libsrcnet_treesitter.so` / `srcnet_treesitter.dll` になる。
[<Literal>]
let Library = "srcnet_treesitter"

/// tree-sitter の位置情報。行と桁はいずれも 0 起点。
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSPoint =
  val Row: uint32
  val Column: uint32

/// tree-sitter の構文ノード。
///
/// 値型で受け渡しされ、内部に所属する木へのポインタを持つ。木より長く生存させては
/// ならないという制約は C API 側の規約であり、managed 側で必ず守る必要がある。
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSNode =
  val Context0: uint32
  val Context1: uint32
  val Context2: uint32
  val Context3: uint32
  val Id: nativeint
  val Tree: nativeint

// C の `bool` は 1 バイトだが、.NET の既定の `bool` marshalling は 4 バイトの
// Win32 BOOL になる。取り違えると未定義動作になるため、境界では `byte` で受けて
// managed 側で比較する。

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint ts_parser_new()

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void ts_parser_delete(nativeint parser)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_parser_set_language(nativeint parser, nativeint language)

/// 解析に費やす時間の上限（マイクロ秒）。0 は無制限。
/// 病的な入力で解析が終わらないことを防ぐ（docs/security.md C-4）。
[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void ts_parser_set_timeout_micros(nativeint parser, uint64 microseconds)

/// 取り消しフラグ。指す領域へ 0 以外を書くと解析が中断される。
[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void ts_parser_set_cancellation_flag(nativeint parser, nativeint flag)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint ts_parser_parse_string(nativeint parser, nativeint oldTree, nativeint source, uint32 length)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void ts_tree_delete(nativeint tree)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_tree_root_node(nativeint tree)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern nativeint ts_node_type(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_node_is_named(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_node_is_null(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_node_has_error(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern uint32 ts_node_start_byte(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern uint32 ts_node_end_byte(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSPoint ts_node_start_point(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSPoint ts_node_end_point(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern uint32 ts_node_child_count(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern uint32 ts_node_named_child_count(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_node_child(TSNode node, uint32 index)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_node_named_child(TSNode node, uint32 index)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_node_parent(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_node_child_by_field_name(TSNode node, nativeint name, uint32 nameLength)

/// 木を走査するカーソル。
///
/// `ts_node_named_child(node, i)` は先頭から数えるため索引に比例した費用がかかり、
/// 子が非常に多いノードを索引で舐めると全体が二乗になる。カーソルは
/// 「最初の子 → 次の兄弟」の連鎖で 1 回の走査を線形に保つ。
[<Struct; StructLayout(LayoutKind.Sequential)>]
type TSTreeCursor =
  val Tree: nativeint
  val Id: nativeint
  val Context0: uint32
  val Context1: uint32
  val Context2: uint32

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSTreeCursor ts_tree_cursor_new(TSNode node)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern void ts_tree_cursor_delete(TSTreeCursor& cursor)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern TSNode ts_tree_cursor_current_node(TSTreeCursor& cursor)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_tree_cursor_goto_first_child(TSTreeCursor& cursor)

[<DllImport(Library, CallingConvention = CallingConvention.Cdecl)>]
extern byte ts_tree_cursor_goto_next_sibling(TSTreeCursor& cursor)

let inline toBool (value: byte) = value <> 0uy

/// tree-sitter が返す文字列は文法が所有する静的領域であり、解放してはならない。
let readStaticString (pointer: nativeint) =
  if pointer = 0n then "" else
  match Marshal.PtrToStringUTF8 pointer with
  | null -> ""
  | text -> text
