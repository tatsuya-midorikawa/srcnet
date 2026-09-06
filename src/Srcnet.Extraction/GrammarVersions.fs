/// 同梱している文法の版。
///
/// **このファイルは `tools/generate_grammar_versions.py` が生成する。手で編集しない。**
///
/// 版は成果物へ記録する決定性の前提条件である（docs/extraction.md 3.2、backlog 020）。
/// 出典は `native/sources.json` のみで、実行時に読み込む経路は作らない
/// （docs/security.md C-1）。
module Srcnet.Extraction.GrammarVersions

/// 文法 1 つ分の記録。
[<Struct>]
type GrammarVersion =
  { /// `native/sources.json` の `language`。
    Language: string
    Version: string
    /// 取得元アーカイブの SHA-256（16 進小文字）。
    Sha256: string }

/// 同梱対象の文法の版。言語名の序数昇順。
///
/// ここに載るのは「同梱対象として構成された文法」であり、実際に構築されたかどうかとは
/// 独立である。実際に利用できた文法との差は `Grammars.available` が示す。
let all: GrammarVersion[] =
  [|
     { Language = "c"
       Version = "v0.23.4"
       Sha256 = "b66c5043e26d84e5f17a059af71b157bcf202221069ed220aa1696d7d1d28a7a" }
     { Language = "cpp"
       Version = "v0.23.4"
       Sha256 = "7a2c55afe3028f4105f25762ea58cc16537d1f5a1dcd9cca90410b3cd5d46051" }
     { Language = "csharp"
       Version = "v0.23.1"
       Sha256 = "c0b008dca3c6820604bf0853b9668ba034f9750d89d170ba834261e94e2cd917" }
     { Language = "fsharp"
       Version = "v0.2.0"
       Sha256 = "cb7aedceb6e215304023558e9192ac84c492a03356c154b10147bff21d605143" }
     { Language = "go"
       Version = "v0.23.4"
       Sha256 = "967870d7d120e9b760e538aeb8331a72f70ffcca4f1eaf1e1dea5375886d25d2" }
     { Language = "java"
       Version = "v0.23.5"
       Sha256 = "cb199e0faae4b2c08425f88cbb51c1a9319612e7b96315a174a624db9bf3d9f0" }
     { Language = "javascript"
       Version = "v0.23.1"
       Sha256 = "fc5b8f5a491a6db33ca4854b044b89363ff7615f4291977467f52c1b92a0c032" }
     { Language = "python"
       Version = "v0.23.6"
       Sha256 = "630a0f45eccd9b69a66a07bf47d1568e96a9c855a2f30e0921c8af7121e8af96" }
     { Language = "rust"
       Version = "v0.23.2"
       Sha256 = "71ce61738f8aff4531afed443572ca68aa74a66a07569a1d50863eb34787c782" }
     { Language = "tsx"
       Version = "v0.23.2"
       Sha256 = "2c4ce711ae8d1218a3b2f899189298159d672870b5b34dff5d937bed2f3e8983" }
     { Language = "typescript"
       Version = "v0.23.2"
       Sha256 = "2c4ce711ae8d1218a3b2f899189298159d672870b5b34dff5d937bed2f3e8983" } |]

/// 構文解析ランタイムの版。文法と同じく決定性の前提条件になる。
[<Literal>]
let RuntimeVersion = "v0.25.10"

/// 言語名から版を引く。同梱対象でなければ `ValueNone`。
let tryFind (language: string) =
  let mutable found = ValueNone

  for entry in all do
    if found.IsNone && System.String.Equals(entry.Language, language, System.StringComparison.Ordinal) then
      found <- ValueSome entry

  found
