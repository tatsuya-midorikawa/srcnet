/// ベンチマークのエントリー ポイント。
///
/// docs/performance.md の目標値はすべて計測前の値である。実測でこれを置き換えるための足場。
/// Release 構成、デバッガー非接続、warmup 実施で実行すること。
module Srcnet.Benchmarks.Program

open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.Emit

/// 既定のツールチェーンは対象フレームワーク用のプロジェクトを生成してビルドするが、
/// 使用している BenchmarkDotNet は net10.0 の生成に対応していない。
/// ホスト プロセス内で実行するツールチェーンへ切り替えて回避する。
/// 前提として、必ず Release 構成・デバッガー非接続で起動すること。
let private configuration =
  DefaultConfig.Instance.AddJob(Job.Default.WithToolchain InProcessEmitToolchain.Instance)

[<EntryPoint>]
let main arguments =
  BenchmarkSwitcher
    .FromAssembly(typeof<HashBenchmarks.ContentHashBenchmarks>.Assembly)
    .Run(arguments, configuration)
  |> ignore

  0
