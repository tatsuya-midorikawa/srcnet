/// 生成物から統計を読み出す。
///
/// マニフェストの件数だけで足りるものはマニフェストから、内訳が要るものは
/// `.files` セグメントを memory-mapped で走査して求める。
module Srcnet.Storage.Stats

open System
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open Srcnet.Core.Graph
open Srcnet.Text

type LanguageCount =
  { Language: Language
    Files: int
    Bytes: int64
    Lines: int64 }

type EncodingCount =
  { Encoding: Encodings.DetectedEncoding
    Files: int }

type FileStatistics =
  { Languages: LanguageCount[]
    Encodings: EncodingCount[]
    TotalBytes: int64
    TotalLines: int64 }

let private languageOfCode (code: uint16) =
  let all =
    [| Unknown
       PlainText
       C
       CHeader
       Cpp
       CppHeader
       ObjectiveC
       ObjectiveCpp
       Rust
       Python
       JavaScript
       TypeScript
       Java
       Go
       CSharp
       FSharp
       Assembly
       Shell
       Makefile
       CMake
       GnBuild
       Kconfig
       Yaml
       Json
       Toml
       Xml
       Markdown
       Owners
       Tsx
       FSharpSignature |]

  match all |> Array.tryFind (fun language -> Language.toCode language = code) with
  | Some language -> language
  | None -> Unknown

let private encodingOfCode (code: uint16) =
  let all =
    [| Encodings.Utf8
       Encodings.Utf8WithBom
       Encodings.Utf16Le
       Encodings.Utf16Be
       Encodings.ShiftJis
       Encodings.EucJp
       Encodings.Iso2022Jp
       Encodings.Gb18030
       Encodings.Big5
       Encodings.EucKr
       Encodings.Binary
       Encodings.Undetermined |]

  match all |> Array.tryFind (fun encoding -> Encodings.toCode encoding = code) with
  | Some encoding -> encoding
  | None -> Encodings.Undetermined

/// `.files` セグメントを走査して言語と符号化の内訳を求める。
let readFileStatistics (outputDirectory: string) (manifest: Manifest.Manifest) : Result<FileStatistics, Reader.OpenError> =
  match
    manifest.Segments
    |> Array.tryFind (fun segment -> segment.Name.EndsWith(".files", StringComparison.Ordinal))
  with
  | None -> Error(Reader.SegmentNotFound "files")
  | Some descriptor ->
    let path = Path.Combine(outputDirectory, descriptor.Name.Replace('/', Path.DirectorySeparatorChar))

    match Reader.MappedSegment.Open path with
    | Error error -> Error error
    | Ok segment ->
      use segment = segment
      let payload = segment.Payload
      let count = int segment.Header.PrimaryCount
      let languageFiles = Dictionary<uint16, int>()
      let languageBytes = Dictionary<uint16, int64>()
      let languageLines = Dictionary<uint16, int64>()
      let encodingFiles = Dictionary<uint16, int>()
      let mutable totalBytes = 0L
      let mutable totalLines = 0L

      let bump (table: Dictionary<uint16, int>) key value =
        match table.TryGetValue key with
        | true, existing -> table[key] <- existing + value
        | false, _ -> table[key] <- value

      let bumpLong (table: Dictionary<uint16, int64>) key value =
        match table.TryGetValue key with
        | true, existing -> table[key] <- existing + value
        | false, _ -> table[key] <- value

      for index in 0 .. count - 1 do
        let record = payload.Slice(index * Format.RecordLength, Format.RecordLength)
        let languageCode = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.FileRecord.LanguageOffset, 2))
        let encodingCode = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(Format.FileRecord.EncodingOffset, 2))
        let lineCount = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(Format.FileRecord.LineCountOffset, 4))
        let sizeBytes = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(Format.FileRecord.SizeOffset, 8))

        bump languageFiles languageCode 1
        bumpLong languageBytes languageCode sizeBytes
        bumpLong languageLines languageCode (int64 lineCount)
        bump encodingFiles encodingCode 1
        totalBytes <- totalBytes + sizeBytes
        totalLines <- totalLines + int64 lineCount

      let languages =
        languageFiles
        |> Seq.map (fun entry ->
          { Language = languageOfCode entry.Key
            Files = entry.Value
            Bytes = languageBytes[entry.Key]
            Lines = languageLines[entry.Key] })
        // 件数の降順。同数は言語コード順で一意に定まる。
        |> Seq.sortWith (fun left right ->
          let byFiles = compare right.Files left.Files

          if byFiles <> 0 then byFiles
          else compare (Language.toCode left.Language) (Language.toCode right.Language))
        |> Seq.toArray

      let encodings =
        encodingFiles
        |> Seq.map (fun entry ->
          { Encoding = encodingOfCode entry.Key
            Files = entry.Value })
        |> Seq.sortWith (fun left right ->
          let byFiles = compare right.Files left.Files

          if byFiles <> 0 then byFiles
          else compare (Encodings.toCode left.Encoding) (Encodings.toCode right.Encoding))
        |> Seq.toArray

      Ok
        { Languages = languages
          Encodings = encodings
          TotalBytes = totalBytes
          TotalLines = totalLines }
