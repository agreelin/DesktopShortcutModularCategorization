# Automatic stage loop policy

The root Codex thread owns one long-running project `/goal` and is the only stage orchestrator.

## Legacy stop-rule interpretation

Any older stage instruction such as `阶段完成后停止，不得自动进入下一阶段` means: stop implementation work for that stage, update evidence, and run `stage_director` before any transition. It does **not** mean that the root long-running goal must wait for another user message. When the gate returns `READY`, the root goal continues automatically. This automatic-loop policy overrides only the old user-wait transition rule; it does not weaken stage scope, verification, safety, approval, or cleanup requirements.

## Stage transition behavior

1. Do not ask the user to provide a stage number when the canonical project documents identify the current stage.
2. At the start of the long-running goal, invoke the project-scoped `stage_director` in read-only mode.
3. After every stage reports completion, invoke `stage_director` again before starting another stage.
4. `stage_director` must inspect `PLAN.md`, `TASKS.md`, `ACCEPTANCE.md`, `DEVLOG.md`, `docs/DECISIONS.md`, `docs/SECURITY.md`, applicable `AGENTS.md`, repository state, and referenced verification evidence.
5. A prose summary such as "all conditions are met" is not sufficient evidence.

## READY

When `stage_director` returns `READY`:

1. Read the complete `NEXT_STAGE_GOAL_PAYLOAD` returned by the agent.
2. Do not display it and wait for another user message.
3. Do not attempt to submit a second slash command from a subagent.
4. Treat the body of the returned `/goal` payload as stage instructions under the existing root goal.
5. Execute the next stage immediately in the root thread.
6. Execute agents serially:
   - invoke `planner` and wait;
   - invoke `coder` and wait for implementation and verification;
   - invoke `reviewer` and wait;
   - on `FAIL`, invoke `coder` to fix only `BLOCKER` and `HIGH`;
   - rerun verification;
   - invoke `reviewer` again.
7. Never run `coder` and `reviewer` in parallel.
8. Never allow multiple write agents to modify the same files concurrently.
9. After the stage reaches its completion gate, invoke `stage_director` again and repeat.

## BLOCKED

When `stage_director` returns `BLOCKED`:

- Stop all stage advancement.
- Do not synthesize a replacement prompt.
- Report the exact blockers and evidence needed.
- Wait for the user only when a genuine decision, approval, missing credential, unsafe operation, or unverifiable gate requires human action.

## PROJECT_COMPLETE

When `stage_director` returns `PROJECT_COMPLETE`:

- Stop the loop.
- Produce the final project summary and retained limitations.
- Do not invent another stage.

## Safety and approval gates

Automatic advancement must stop before any next stage that requires a new explicit user approval under `AGENTS.md`, `docs/DECISIONS.md`, `docs/SECURITY.md`, `PLAN.md`, or `ACCEPTANCE.md`.

Automatic advancement must never bypass:

- Windows security or elevation approvals;
- audit policy or SACL approval gates;
- real-user-directory restrictions;
- signing or protected-installation decisions;
- destructive or irreversible operations;
- unresolved ACL/SACL cleanup;
- `RecoveryRequired` or unknown security state;
- unresolved `BLOCKER` or `HIGH` findings.

## Limits

- Maximum automatic transitions in one root goal: 8.
- Maximum repair iterations within one stage: use the stricter documented stage limit; default 6 when absent.
- Repeating the same failed remediation twice must stop the stage.
- The root thread may auto-advance only one stage at a time: gate, execute one stage, gate again.
