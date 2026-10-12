using Xunit;

// The services under test keep process-level state on purpose: Paths.DataDir is set once at
// startup, and the approved skill dictionary lives in Keywords as a static so that scoring,
// ranking and the prompt builders can read it without a list threaded through all of them.
//
// xUnit runs test classes in parallel by default, which means two TestHosts in flight at once
// point Paths.DataDir at different temp databases and clear each other's skill dictionary. The
// symptom is a test that passes alone and fails in a full run — exactly the kind of flake that
// gets dismissed as "flaky, retry it". The suite runs in about a second, so serialising it costs
// nothing and removes the whole class of problem.
[assembly: CollectionBehavior(DisableTestParallelization = true)]