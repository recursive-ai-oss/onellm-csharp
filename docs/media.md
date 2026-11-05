# Media: images, speech, transcription and moderation

Four capability interfaces cover the non-chat endpoints of OpenAI and Gemini. Each result carries a `Cost` from the pricing engine and, where the
service reports one, a `Usage`.

| Interface | Method | OpenAI | Gemini |
|---|---|---|---|
| `IImageProvider` | `GenerateImageAsync` | `/images/generations`, `/images/edits` (input images and a mask as data URLs), partial images by SSE through `IImageStreamingProvider` | `generateContent` with the `IMAGE` response modality; one call per image when `Count > 1`, usage summed |
| `ITextToSpeechProvider` | `SynthesizeAsync` | `/audio/speech`, audio streaming through `ISpeechStreamingProvider` | `generateContent` with the `AUDIO` modality: prebuilt voice, multi-speaker voices; raw PCM is wrapped as WAV unless `pcm` is asked for |
| `ISpeechToTextProvider` | `TranscribeAsync` | `/audio/transcriptions` and `/audio/translations` (multipart), SSE text deltas through `ISpeechToTextStreamingProvider` | none: audio understanding is chat |
| `IModerationProvider` | `ModerateAsync` | `/moderations`, texts and images in one request | none |

Not implemented: Gemini's newer Interactions API for image and speech (the models are reached through `generateContent`), Gemini media streaming,
video generation and realtime voice.

Media in requests and results is bytes plus a content type (`ImageInput`, `GeneratedImage`, `SpeechResponse.Audio`). Settings a model does not have (for
example `quality` on a Gemini image model) are dropped and reported in the response's `Warnings`.

## How usage is priced

The pricing rules are in `pricing-and-counting.md`; media models use these counters.

| Counter | Used by | Notes |
|---|---|---|
| `inputTokens`, `outputTokens` | image, speech and transcription models that bill tokens | text rates from `inputPerMillion` / `outputPerMillion` |
| `inputImageTokens`, `outputImageTokens` | OpenAI and Gemini image models | subsets of `inputTokens` / `outputTokens`: the engine prices them at their own rate and takes them out of the text totals |
| `outputAudioTokens` | Gemini speech models | subset of `outputTokens` |
| `characters` | `tts-1`, `tts-1-hd` | UTF-16 length of the input, priced per million |
| `audioSeconds` | `whisper-1`, `gpt-transcribe`, and the transcription models when a response reports a duration but no tokens | per-minute prices are written per second so the arithmetic is exact |

Moderation models are free: their cost is a total of zero. Batch mode applies the model's `batch` multiplier.

`gpt-4o-mini-tts` bills tokens but the speech endpoint reports no usage, so its `Cost` is null and the response carries a `usage_missing` warning;
OneLLM does not estimate. A transcription response with neither usage nor a duration is treated the same way.

## Tools

`MediaTools.Create(provider, imageModel, speechModel, transcriptionModel, moderationModel, options)` returns the tools the provider supports for the
models given: `generate_image`, `speak`, `transcribe` and `moderate`. The host picks the model; the model of the conversation never names one.
`MediaToolOptions` sets the input size limit (25 MiB), the most images per call (4), the longest text to speak (4096 characters), a cost hint,
whether a person must approve each call, and the timeout (five minutes).

- **Cost.** A tool adds the provider's cost lines to its `ToolContext`; the invoker puts them on the `ToolResult`.
- **Outputs.** Images and audio come back as asset parts with inline data (storage `inline`); the runtime's asset store decides where they live. The
  tools write no files. `moderate` returns JSON with the flagged categories and scores per input.
- **Files.** Paths in `images`, `mask` and `path` are read through the runtime's file grant, the same way the built-in file tools read: the path is
  resolved with symbolic links followed and must lie under a read root (`ICapabilities.ResolveRead`), and the file is opened with the built-ins'
  double check (`docs/capabilities.md`). A path outside the roots, a run with no file grant, a file over the size limit and a missing file are error
  results that name the argument, before any provider call. `transcribe` declares the file-read grant (`ICapabilityTool`); the other tools need no
  grant unless the model names an input file. The tools write no files and fetch no URLs, so the write roots and the egress policy are not involved.
- **Network.** The call to the provider is the use of the network the host chose when it gave the tool a client. It is not tool network egress:
  `ICapabilities.IsNetworkAllowed` governs tools that open connections of their own, such as `http_request`, and is not consulted here.
