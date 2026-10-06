# Models, providers and routing

How resu-clean talks to models, how it finds out which models exist, and what happens when one
fails.

**Last reviewed: 2026-10-06.** Provider base URLs and endpoints change; re-check before relying on
a preset.

## The model is optional

Everything deterministic works with no provider configured: cleaning, ATS scoring, keyword matching,
job search, and template application kits. Models only add rewording, prioritised fixes, tailoring,
and written emails. `GET /api/capabilities` reports which features need one, and the UI says so
rather than failing.

## Two provider types

| Type | Chat API | Auth header | Model list |
|---|---|---|---|
| `openai` | `POST {base}/chat/completions` | `Authorization: Bearer <key>` | `GET {base}/models` |
| `anthropic` | `POST {base}/v1/messages` | `x-api-key: <key>` + `anthropic-version` | `GET {base}/v1/models` |

`openai` means *OpenAI-compatible*, which covers most aggregators and local servers. Anything that
speaks the OpenAI chat-completions shape works with `openai`, regardless of its name.

## Presets

Selecting a preset in the UI fills in the type, base URL, environment variable, a key hint and a
link to where you get a key. It saves nothing on its own.

| Preset | Type | Base URL | Key env var | Notes |
|---|---|---|---|---|
| OpenAI | `openai` | `https://api.openai.com/v1` | `OPENAI_API_KEY` | Reference implementation |
| Anthropic | `anthropic` | `https://api.anthropic.com` | `ANTHROPIC_API_KEY` | Native Messages API |
| OpenRouter | `openai` | `https://openrouter.ai/api/v1` | `OPENROUTER_API_KEY` | Many models behind one key; the list is large |
| Groq | `openai` | `https://api.groq.com/openai/v1` | `GROQ_API_KEY` | Fast inference on open models |
| Google Gemini | `openai` | `https://generativelanguage.googleapis.com/v1beta/openai` | `GEMINI_API_KEY` | Google's official OpenAI-compatible endpoint |
| NVIDIA NIM | `openai` | `https://integrate.api.nvidia.com/v1` | `NVIDIA_API_KEY` | Hosted NIM endpoints |
| Clean APIs | `openai` | `https://cleanapis.com/v1` | `CLEANAPIS_API_KEY` | Aggregator; key needs `models:read` |
| Ollama | `openai` | `http://127.0.0.1:11434/v1` | *(none)* | Local; start Ollama first |
| LM Studio | `openai` | `http://127.0.0.1:1234/v1` | *(none)* | Local; start the server first |

Sources for each: OpenAI platform docs, [Anthropic console](https://console.anthropic.com/settings/keys),
[Groq OpenAI compatibility](https://console.groq.com/docs/openai),
[Gemini OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai),
[NVIDIA NIM API reference](https://docs.nvidia.com/nim/large-language-models/latest/api-reference.html),
[Clean APIs getting started](https://cleanapis.com/docs/getting-started).

## Fetching the model list

Press **Fetch models from the provider** and resu-clean calls the provider's own listing endpoint and
shows you the real ids, so you pick rather than guess. Three response shapes exist in the wild and all
are handled:

| Shape | Example | Source |
|---|---|---|
| OpenAI-style | `{ "data": [ { "id": "gpt-4o-mini" } ] }` | OpenAI, Groq, OpenRouter, Clean APIs, NVIDIA |
| `name` not `id` | `{ "data": [ { "name": "llama3.2:latest" } ] }` | Ollama's compatibility layer |
| Gemini native | `{ "models": [ { "name": "models/gemini-2.5-pro" } ] }` | Google |

Two normalisations happen automatically:

- **Gemini's `models/` prefix is stripped**, so you get `gemini-2.5-pro` rather than
  `models/gemini-2.5-pro`. The prefix is only stripped for Google's host; another provider returning
  a `models/` prefix keeps it.
- **Non-chat models are hidden.** Anything matching embed/embedding/rerank/whisper/tts/imagen/veo/
  audiogen/moderation/guard/clip cannot serve a chat completion, so routing to one would only ever
  fail. Embedding and image models are filtered out of the picker.

If the model list fails, the UI tells you which of the common causes it hit — rejected key or missing
`models:read` scope, no `/models` endpoint at that URL, a local server that is not running — and you
can always type ids by hand. Saving a provider with an empty model list triggers one best-effort
fetch; a failure there never blocks the save.

## Routing and fallback

Each of six workflows (`clean`, `ats`, `profile`, `update_resume`, `find_jobs`, `apply_email`) has an
**ordered list** of `{provider, model}` pairs. On a call:

1. Try the first entry.
2. If it is missing a key, disabled, removed, the model name is empty, or the provider returns an
   error — note the reason and move to the next entry.
3. An empty reply counts as a failure and falls through too.
4. The first success wins and is returned with the provider and model that served it.
5. If every entry fails, the workflow reports "no model" and **falls back to its deterministic
   behaviour** rather than erroring. A clean still produces cleaned text; an ATS scan still scores;
   a kit still gets the template email.

Fallback is per workflow, so you can send `clean` to a cheap fast model and `update_resume` to a
stronger one, each with their own fallback chain.

```
clean          1. groq/llama-3.3-70b-versatile
               2. openrouter/anthropic/claude-sonnet-4.5
               3. ollama/llama3.2
```

One line per entry, `providerId::model`. *Save* persists it, *Live test* makes a real call and shows
the reply or the exact error.

## The no-fabrication guard

Model output is never trusted. Every model-assisted workflow:

1. **Feeds only your facts.** The system prompt carries your approved profile as the only source of
   truth, with explicit rules: never invent employers, titles, dates, metrics or skills; mirror job
   wording only where it is already true; keep every number and date exactly as supplied; never
   return a placeholder.
2. **Validates the output.** Before you see it, the output is compared against its input. Any number,
   date or unknown proper noun that is not present in the input causes the output to be **discarded**
   and the deterministic version used instead, with a warning telling you what was rejected. Unknown
   proper nouns that survive are flagged for you to check rather than silently accepted.
3. **Queues new information.** Anything the model claims as new goes to the pending-queue and needs
   your approval before it joins your profile. If a tailoring instruction contains unapproved
   information, the run **stops** and asks, rather than writing.

## Where keys live

- Stored keys are encrypted before they touch the database: **DPAPI (CurrentUser)** on Windows, or
  **AES-256** with a machine-local key file at `data/machine.key` (mode 0600) elsewhere.
- **No endpoint returns a key**, including `GET /api/providers`. You get `hasKey` and `keySource`
  (`stored`, `env`, `none`) and nothing else.
- Resolution order: stored key first, then the named environment variable.
- Updating a provider without a key keeps the stored one.
- Errors redact anything key-shaped before they reach the log.
- **Prefer an environment variable in `.env`** — then the key never touches the database at all.

## OpenAI-compatible local servers

Ollama and LM Studio need no key. resu-clean treats `localhost`/`127.0.0.1` bases as keyless, so it
will not complain about a missing one, and it will offer to fetch the model list from whatever you
have loaded. Start the server first; the failure message says so if nothing answers.