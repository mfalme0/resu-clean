<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type RehearsalState, type RehearsalQuestion, type RehearsalAnswerRow } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  const params = new URLSearchParams(window.location.search);
  let jobId = params.get('jobId') ?? '';

  let state: RehearsalState | null = null;
  let loading = true;
  let answering = false;
  let currentAnswer = '';
  let currentQuestionIndex = -1;

  async function load() {
    if (!jobId) {
      loading = false;
      return;
    }
    try {
      state = await api.get<RehearsalState>(`/jobs/${jobId}/rehearsal`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      loading = false;
    }
  }

  onMount(() => load());

  function answeredCount(): number {
    const answered = new Set(state?.answers.map((a) => a.question) ?? []);
    return state?.questions.filter((q) => answered.has(q.question)).length ?? 0;
  }

  function ungroundedCount(): number {
    return state?.answers.filter((a) => !a.grounded).length ?? 0;
  }

  $: answersByQuestion = new Map((state?.answers ?? []).map((a) => [a.question, a]));

  function startAnswer(index: number, question: RehearsalQuestion) {
    currentQuestionIndex = index;
    currentAnswer = '';
  }

  async function submitAnswer(question: RehearsalQuestion) {
    if (!currentAnswer.trim()) return;
    answering = true;
    try {
      const result = await api.post<RehearsalAnswerRow>(`/jobs/${jobId}/rehearsal/answer`, {
        question: question.question,
        answer: currentAnswer.trim()
      });
      await load();
      flash(result.grounded ? 'Answered — your figures check out.' : 'Answered — some figures are not in your verified profile.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      answering = false;
      currentQuestionIndex = -1;
      currentAnswer = '';
    }
  }

  function isAnswered(question: RehearsalQuestion): boolean {
    return (state?.answers ?? []).some((a) => a.question === question.question);
  }

  function getAnswer(question: RehearsalQuestion): RehearsalAnswerRow | undefined {
    return (state?.answers ?? []).find((a) => a.question === question.question);
  }

  function versionSelectPlaceholder(): string {
    return 'Select a resume version first';
  }
</script>

<svelte:head><title>Rehearse &middot; resu-clean</title></svelte:head>

{#if loading}
  <p class="meta loading">Loading&hellip;</p>
{:else if !jobId}
  <section class="intro">
    <span class="label">Section six</span>
    <h1>Rehearse the<br />interview.</h1>
    <p class="justify lede dropcap">
      Pick a saved job, read the questions its posting is likely to produce, and answer them here.
      Every figure you quote is checked against your verified profile: if nothing in your profile
      backs a number you are about to say out loud, the tool tells you before the interview room.
    </p>
  </section>

  <div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

  <section class="block">
    <Rule text="Choose a job" note="questions come from that posting" />
    <p class="meta">
      Open a saved job in Jobs, then come back here with its id. Or add the job id below to rehearse
      for any role you have saved.
    </p>
    <label class="label" for="job-id">Saved job id</label>
    <input id="job-id" class="input" bind:value={jobId} placeholder="job_xxxx" />
    <button type="button" class="btn btn-primary" on:click={load} disabled={!jobId.trim()}>Load questions</button>
  </section>
{:else if state === null}
  <Notice tone="error" title="Could not load rehearsal" message="Make sure the job id is right and a resume version is marked best-fit." />
{:else}
  <section class="intro">
    <span class="label">Section six</span>
    <h1>Rehearse the<br />interview.</h1>
    <p class="justify lede dropcap">
      Answer each question as you would in the room. Every figure you quote is checked against your
      verified profile: ungrounded numbers are flagged before you say them out loud.
    </p>
  </section>

  <div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

  <section class="block">
    <Rule text="Progress" note="{answeredCount()} of {state.questions.length} answered &middot; {ungroundedCount()} flagged" />

    <div class="tally">
      <div class="tally-cell">
        <span class="label">Questions</span>
        <span class="tally-figure">{state.questions.length}</span>
      </div>
      <div class="tally-cell">
        <span class="label">Answered</span>
        <span class="tally-figure">{answeredCount()}</span>
      </div>
      <div class="tally-cell">
        <span class="label">Ungrounded figures</span>
        <span class="tally-figure">{ungroundedCount()}</span>
      </div>
    </div>

    {#if state.usedModel}
      <p class="meta">Questions written by the routed model. Otherwise they come from the deterministic pass, which reads your verified profile gaps instead.</p>
    {/if}
  </section>

  <section class="block">
    <Rule text="Questions" note="{state.questions.length} from this posting" />

    {#if state.questions.length === 0}
      <Empty
        title="No questions yet"
        message="The deterministic pass found no risky claims on your resume, or this posting has no text to read. Add the job's description in Jobs and try again."
        action="Open Jobs to add a posting."
      >
        <button type="button" class="btn" on:click={() => { window.location.href = '/jobs'; }}>Open Jobs</button>
      </Empty>
    {:else}
      <div class="list">
        {#each state.questions as q, i (i)}
          <article class="question" class:answered={isAnswered(q)} class:current={currentQuestionIndex === i}>
            <header class="question-head">
              <span class="q-number">{i + 1}</span>
              <div class="q-main">
                <h3 class="q-text">{q.text}</h3>
                {#if q.risk}
                  <span class="badge badge-warn">ungrounded claim</span>
                {/if}
                <p class="q-anchor">asks about “{q.groundedIn}”</p>
                {#if q.riskNote}<p class="q-risk">{q.riskNote}</p>{/if}
              </div>
              <button
                type="button"
                class="btn btn-ghost btn-sm"
                class:active={currentQuestionIndex === i}
                on:click={() => startAnswer(i, q)}
                aria-label="Answer question {i + 1}"
              >
                {currentQuestionIndex === i ? 'Close' : 'Answer'}
              </button>
            </header>

            {#if currentQuestionIndex === i}
              <div class="answer-area">
                <label class="sr-only" for={`answer-${i}`}>Your answer</label>
                <textarea
                  id={`answer-${i}`}
                  class="textarea"
                  bind:value={currentAnswer}
                  placeholder="Type your answer as you would say it in the interview. Mention numbers, years, percentages — anything you would claim under pressure."
                ></textarea>
                <p class="meta">
                  Every figure you type is checked against your verified profile. A number that appears
                  nowhere there is flagged — not because it is wrong, but because you cannot prove it
                  yet.
                </p>
                <div class="answer-actions">
                  <button type="button" class="btn" on:click={() => { currentQuestionIndex = -1; currentAnswer = ''; }}>Cancel</button>
                  <button type="button" class="btn btn-primary" on:click={() => submitAnswer(q)} disabled={!currentAnswer.trim() || answering}>
                    {answering ? 'Saving…' : 'Save answer'}
                  </button>
                </div>
              </div>
            {:else if isAnswered(q)}
              {@const a = answersByQuestion.get(q.question)}
              <div class="answer-shown">
                {#if a}
                  <div class="answer-result" class:ungrounded={!a.grounded}>
                    <span class="badge" class:badge-pass={a.grounded} class:badge-fail={!a.grounded}>
                      {a.grounded ? 'grounded' : 'flagged'}
                    </span>
                    <p>{a.feedback}</p>
                    <p class="meta answer-text">Your answer: “{a.answer.length > 200 ? a.answer.slice(0, 200) + '…' : a.answer}”</p>
                  </div>
                {/if}
              </div>
            {/if}
          </article>
        {/each}
      </div>
    {/if}
  </section>

  <section class="block">
    <Rule text="What this checks" />
    <p>
      The tool pulls every figure from your answer — numbers, percentages, years, “k”, “million” — and
      checks whether the same figure appears anywhere in your verified profile. If it does not, the
      answer is flagged. That does not mean the figure is false; it means you cannot prove it from
      facts you have already approved, which is exactly what an interviewer will ask.
    </p>
    <p>
      Nothing here is sent anywhere. Your answers, the questions, and the grounding check all stay in
      the SQLite database in your <span class="mono">data/</span> folder. No model sees your answers
      unless you have routed one for this workflow — and even then, the grounded/ungrounded verdict is
      computed locally before anything leaves this machine.
    </p>
  </section>
{/if}

<style>
  .intro {
    max-width: 60ch;
  }

  .lede {
    margin-top: var(--s4);
  }

  .block {
    margin-top: var(--s7);
  }

  .tally {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
    gap: var(--s4);
    margin-bottom: var(--s5);
  }

  .tally-cell {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    background: var(--paper);
  }

  .tally-cell.answered {
    border-left-color: var(--accent);
  }

  .tally-figure {
    font-family: var(--font-display);
    font-size: 2rem;
    font-weight: 900;
    line-height: 1;
    color: var(--ink);
  }

  .label {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--neutral-600);
    margin-top: var(--s1);
  }

  .list {
    display: grid;
    gap: var(--s4);
  }

  .question {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
  }

  .question.answered {
    background: var(--neutral-25);
  }

  .question.current {
    border-left-color: var(--accent);
    background: var(--neutral-50);
  }

  .question-head {
    display: flex;
    gap: var(--s4);
    align-items: flex-start;
    flex-wrap: wrap;
    margin-bottom: var(--s3);
  }

  .q-number {
    font-family: var(--font-display);
    font-size: 1.5rem;
    font-weight: 900;
    color: var(--neutral-400);
  }

  .q-main {
    flex: 1;
    min-width: 0;
  }

  .q-text {
    font-size: 1.125rem;
    margin-bottom: var(--s1);
  }

  .q-anchor {
    font-family: var(--font-mono);
    font-size: 0.8125rem;
    color: var(--neutral-600);
    margin-bottom: var(--s1);
  }

  .q-risk {
    font-size: 0.875rem;
    color: var(--accent);
    font-style: italic;
    margin-top: var(--s1);
  }

  .badge {
    font-family: var(--font-mono);
    font-size: 0.6875rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    padding: 2px 8px;
    border: 1px solid currentColor;
    background: transparent;
  }

  .badge-warn {
    color: var(--accent);
  }

  .badge-pass {
    color: var(--ink);
    border-color: var(--ink);
  }

  .badge-fail {
    color: var(--accent);
    border-color: var(--accent);
  }

  .answer-area {
    margin-top: var(--s3);
    border-top: var(--hair);
    padding-top: var(--s4);
  }

  .answer-actions {
    display: flex;
    gap: var(--s2);
    margin-top: var(--s3);
  }

  .answer-shown {
    margin-top: var(--s3);
    border-top: var(--hair);
    padding-top: var(--s4);
  }

  .answer-result {
    padding: var(--s3);
    background: var(--paper);
    margin-bottom: var(--s2);
  }

  .answer-result.ungrounded {
    border-left: 3px solid var(--accent);
  }

  .answer-text {
    font-family: var(--font-mono);
    font-size: 0.8125rem;
    color: var(--neutral-600);
    margin-top: var(--s1);
  }

  .textarea {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    min-height: 100px;
    width: 100%;
    border: var(--hair);
    padding: var(--s3);
    box-sizing: border-box;
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  .mono {
    font-family: var(--font-mono);
  }

  .loading {
    font-family: var(--font-mono);
    letter-spacing: 0.18em;
    text-transform: uppercase;
  }

  @media (max-width: 767px) {
    .question-head {
      flex-direction: column;
    }

    .q-number {
      font-size: 1.25rem;
    }
  }
</style>