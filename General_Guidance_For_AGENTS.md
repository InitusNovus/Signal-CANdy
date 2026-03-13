# General_Guidance_For_AGENTS.md

## Purpose

This document is a **general guidance reference** for AI coding agents that are asked to create a repository-specific `AGENTS.md`.

It is **not** intended to be copied verbatim as the final `AGENTS.md` for every repository.
Instead, it defines the **recommended structure, reusable policies, reporting discipline, execution style, and evidence-handling rules** that should be adapted into each repository's own `AGENTS.md`.

The generated repository-specific `AGENTS.md` should:

- preserve the repository's actual project identity and boundaries
- clearly separate general reusable rules from repo-specific instructions
- keep reporting and recovery behavior highly consistent across repositories
- remain useful for long-running, interruptible, evidence-based agent work

---

## How This Document Should Be Used

When generating a repository-specific `AGENTS.md`, treat this document as the **general operating standard**.

Use it to define:

- execution philosophy
- source-of-truth hierarchy
- reporting rules
- run identity and recovery behavior
- immutable history policy
- patch-forward correction policy
- execution style
- skill/tool usage boundaries
- validation expectations
- local-only workspace conventions where relevant

Do **not** blindly copy repository-specific examples from another project.
Instead:

1. keep the reusable operating rules
2. replace project identity and domain-specific details
3. add repo-specific modes, directories, artifacts, targets, and constraints
4. preserve the reporting discipline as consistently as possible

---

## Recommended High-Level Structure for a Repo-Specific `AGENTS.md`

A repository-specific `AGENTS.md` should usually contain:

1. `Project Identity`
2. `Core Philosophy`
3. `Project Operation Modes`
4. `Source-of-Truth Rules`
5. `Quality Bar`
6. `Output Structure` or `Repository Structure`
7. `Reporting Rules`
8. `Run Identity and Recovery Records`
9. `Evidence and Traceability`
10. `Execution Style`
11. `Immutable History and Patch-Forward Correction`
12. `Validation Expectations`
13. `Skill / Tool Usage Policy`
14. `Local-Only Workspace Policy` where applicable
15. `Reference vs Implementation Boundary` where applicable

The exact section names may vary slightly, but the meaning should remain stable.

---

## Core Philosophy Guidance

A strong repository-specific `AGENTS.md` should define a clear operating philosophy.

Recommended reusable principles:

- Treat actual repository contents, actual source files, and actual local evidence as primary truth.
- Do not casually fill gaps with generic best practices, folklore, or assumed intent.
- Prefer precise, bounded statements over vague generalities.
- Explicitly mark uncertainty when evidence is incomplete.
- Record important decisions instead of relying on implied memory.
- Preserve interruption recovery as a first-class requirement.
- Favor auditability and traceability over smooth-looking but unsupported conclusions.

Useful wording patterns:

- `Primary evidence`
- `Secondary support`
- `Unclear from evidence`
- `Not explicitly evidenced`
- `Inferred (low confidence)`

These patterns help maintain honesty and make later reviews easier.

---

## Project Operation Modes Guidance

A repository-specific `AGENTS.md` should state its operation modes clearly.

Mode names will vary by repository, but the intent should be explicit.
Common reusable patterns include:

- `Bootstrap Mode`
- `Refinement Mode`
- `Comparative Mode`
- `Audit Mode`
- `Pilot Mode`
- `Registry Maintenance Mode`
- `Distribution / Packaging Maintenance Mode`

Guidance:

- Execution prompts should state clearly which mode is active.
- Report emphasis should vary by mode.
- Mode changes should be explicit, not implied.
- A materially different scope may justify a new run, a new report set, and a new `RUN_ID`.

---

## Source-of-Truth Rules Guidance

Every repository-specific `AGENTS.md` should define a clear source hierarchy.

Recommended pattern:

- actual repo code / actual workspace files / actual local artifacts = primary evidence
- supporting skills, helper corpora, previous notes, and higher-level references = secondary support
- generic assumptions = unacceptable unless explicitly labeled as uncertainty

Recommended rules:

- Do not let secondary aids override direct primary evidence.
- When something is not explicitly supported by source evidence, say so clearly.
- Do not convert absence of evidence into false certainty.
- If a later file omits something, do not assume that automatically means deprecation.
- File location, folder naming, or naming convention alone may be a clue, not a complete truth.

---

## Quality Bar Guidance

Each repo-specific `AGENTS.md` should define quality expectations that remain high even under uncertainty.

Recommended rules:

- Do not lower quality just because some parts remain provisional.
- Produce the best evidence-grounded result the repository can justify.
- Strong sections should be written strongly.
- Weak sections should be explicitly bounded, not padded.
- Production-quality outputs should not be downgraded into draft-quality just because recovery/reporting is emphasized.
- Every included artifact, module, design choice, or structural decision should be justifiable from evidence.

---

## Reporting Rules

This section is intended to be highly reusable across repositories.
For many repositories, it can be adopted with only small adjustments.

### Core Reporting Principle

Each execution batch must generate reports under `/Reports/`.

Reports are not optional convenience notes.
They are part of the repository's working memory and must support:

- interruption recovery
- later review
- auditability
- traceability of decisions
- clarification of what was and was not proved

### Reports Directory Policy

- All run reports must be written under `/Reports/`.
- Existing report files in `/Reports/` are **immutable historical records** and **MUST NOT** be modified after the run/session that created them has ended.
- Each report captures the exact state, scope, assumptions, and decisions of that run.

### RUN_ID Convention

Use `RUN_ID` format:

- `yyyymmdd-hhmm`

Timezone:

- `KST timezone`

Guidance:

- The **first report created for an execution batch establishes the canonical RUN_ID** for that batch.
- All later reports produced for the **same unfinished batch** MUST reuse that exact canonical `RUN_ID`.
- This remains true even if the work is interrupted, resumed in a new session, or completed hours later.
- A **new `RUN_ID`** may be created only when:
  - the previous batch is explicitly closed, such as by final summary / handoff closure, or
  - the user starts a materially new execution scope
- Do **not** mint a fresh `RUN_ID` just because wall-clock time changed while the same batch is still in progress.

### RUN_ID Correction Rule

If a `RUN_ID` correction is ever truly unavoidable:

- correct the **filename and internal RUN_ID together**
- add a top-level `Correction Note`
- state:
  - original value
  - corrected value
  - reason for the exception

Such correction should be rare.

### Mandatory Core Reports (All Non-Trivial Runs)

Every non-trivial execution should produce at minimum:

- `Reports/[RUN_ID]-Prompt_Record.md`
- `Reports/[RUN_ID]-Workspace_Snapshot.md`
- `Reports/[RUN_ID]-Final_Execution_Summary.md`

In addition, every non-trivial run should produce:

- at least one mode-specific report appropriate to the current operation mode

Examples of mode-specific reports:

- `Reports/[RUN_ID]-Capability_Audit.md`
- `Reports/[RUN_ID]-Pilot_Attempt.md`
- `Reports/[RUN_ID]-Design_Decision.md`
- `Reports/[RUN_ID]-Validation_Report.md`
- `Reports/[RUN_ID]-Comparative_Analysis.md`
- `Reports/[RUN_ID]-Implementation_Progress.md`
- `Reports/[RUN_ID]-Registry_Update.md`
- `Reports/[RUN_ID]-Skill_Distribution_Check.md`

The exact names may vary by repository, but the **three core reports** should remain as stable as possible.

### Prompt Record Requirement

`Reports/[RUN_ID]-Prompt_Record.md` must contain the **full input execution prompt as-is**.

Rules:

- preserve the actual prompt verbatim
- do not replace it with a summary
- do not silently rewrite the instruction set
- preserve enough fidelity for later resume/review

Purpose:

- interruption recovery
- traceability of instructions
- reconstruction of execution intent
- comparison between prompt and actual output

### Workspace Snapshot Requirement

`Reports/[RUN_ID]-Workspace_Snapshot.md` should capture the initial project state before deeper processing begins.

Include, when applicable:

- relevant source roots
- relevant working directories
- discovered targets / modules / source trees / packages
- detected branches / files / artifacts / registries
- existing report files or prior run context
- current local wiring that matters to the run
- missing paths
- unexpected paths
- known boundary conditions

This report is especially important for recovery after interruption.

### Final Execution Summary Requirement

`Reports/[RUN_ID]-Final_Execution_Summary.md` should summarize:

- what was accomplished
- what remains unfinished
- what was proven
- what was not proven
- key decisions
- blockers or unresolved uncertainty
- handoff state for future continuation

This report should be honest about incompleteness.
Partial success must remain visible as partial success.

### Timestamp Fields

Each run summary report should include:

- `run_started_at`: actual run start timestamp matching the `RUN_ID`
- `report_written_at`: timestamp when that specific report file was written

If they differ significantly, note this explicitly.
This is normal for long-running or resumed work.

### Reporting Behavior by Mode

Report emphasis should vary by mode.

Examples:

- `Bootstrap Mode`
  - discovery results
  - initial onboarding state
  - first-pass output quality
- `Refinement Mode`
  - cleanup scope
  - normalization results
  - wording/speculation fixes
  - confidence improvements
- `Comparative Mode`
  - comparison axes
  - similarity/difference analysis
  - portability implications
- `Audit / Probe Mode`
  - what was inspected
  - what evidence exists
  - what remains unproven
- `Pilot Mode`
  - exactly what was attempted
  - exact stop boundary
  - exactly what the pilot did and did not prove
- `Registry Maintenance Mode`
  - state transitions
  - newly discovered entities
  - readiness changes
- `Distribution / Packaging Maintenance Mode`
  - path migration checks
  - stale reference checks
  - distribution integrity checks

### Reporting Obligation

After every non-trivial execution batch, a `RUN_ID`-based report set **MUST** be written to `/Reports/`.

No silent work.
No unreported non-trivial batch.
No replacing structured reports with vague conversational memory.

---

## Immutable History and Patch-Forward Correction

This section is strongly recommended for reuse with minimal edits.

### Immutability Rule

Existing report files in `/Reports/` are **immutable historical records**.

They **MUST NOT** be modified after the session that created them ends.

Reason:

- they preserve what was believed and done at that time
- they support auditability
- they support recovery
- they preserve historical decision context

### Patch-Forward Correction Policy

If a later run discovers that a previous report contained:

- inaccurate claims
- stale paths
- broken assumptions
- drift from current truth
- incomplete or misleading status

then:

1. **Do NOT modify the original report.**
2. Record the correction in the **current run's** report under a clearly labeled section such as:
   - `Corrections to Prior Reports`
   - `Drift Notes`
3. Reference:
   - the original report's `RUN_ID`
   - the specific claim being corrected
4. Provide the corrected information with evidence.

Summary principle:

**History is immutable. Current truth is patch-forward.**

---

## Evidence and Traceability Guidance

Important repository decisions should be traceable.

For each important decision, selection, inclusion/exclusion, or interpretation, record as appropriate:

- source path
- artifact name
- file name
- branch or version when relevant
- rationale
- confidence
- modifications made, if any
- whether the statement is direct evidence or inference

Recommended confidence labels:

- `High`
- `Medium`
- `Low`

Recommended uncertainty labels:

- `Unclear from source evidence`
- `Not explicitly evidenced`
- `Inferred (low confidence)`

---

## Execution Style Guidance

A reusable repo-specific `AGENTS.md` should usually encourage this execution style:

- work autonomously within the stated scope
- avoid unnecessary confirmation requests mid-run
- preserve useful intermediate state in reports
- adapt sensibly when encountering missing files or ambiguous layouts
- record ambiguity rather than hiding it
- treat interruption recovery as a first-class concern
- distinguish clearly between evidence, interpretation, and speculation
- do not present partial validation as full proof

For interactive or partially interactive systems, the AGENTS file may additionally define a bounded checkpoint model, such as:

- human acknowledgment boundary
- approval-gated mutation boundary
- bounded operator-assisted transition point

But such boundaries must be stated explicitly.

---

## Validation Expectations

Each repo-specific `AGENTS.md` should define validation in a way that matches the repository's actual purpose.

Recommended general categories:

### Structural Validation

Examples:

- referenced files exist
- referenced directories exist
- declared paths match reality
- required docs / registries / scripts / assets are present
- active docs describe the current repo truth, not inherited stale truth

### Evidence Validation

Examples:

- major claims are tied to concrete evidence
- unsupported claims are labeled as uncertainty
- assumptions are not silently promoted into facts
- source-of-truth hierarchy is followed consistently

### Reporting Quality Validation

Examples:

- reports follow the `RUN_ID` naming convention
- required reports are present for the current mode
- `Prompt_Record` captures the actual prompt, not a summary
- `Workspace_Snapshot` reflects the real initial state
- `Final_Execution_Summary` accurately distinguishes complete vs incomplete work

### Mode-Specific Validation

Examples:

- pilot runs state exactly what was and was not proved
- approval-gated mutation remains approval-gated
- local-only experiments are not misrepresented as canonical repo truth
- downstream handoff claims are bounded by actual evidence

### Validation Honesty Rule

All validation results must be reported honestly.

Partial failures must be documented as partial failures.
Unproven claims must remain unproven.
Missing evidence must remain visible.

---

## Skill / Tool Usage Policy Guidance

A repository-specific `AGENTS.md` should define tool and skill usage boundaries clearly.

Reusable guidance:

- skills and helper tools may be used where they genuinely help
- they are secondary support unless the repo explicitly defines otherwise
- primary evidence remains the actual repository/workspace material
- tool output must not override direct source evidence without justification
- self-referential use of a repository's own skill/tool should be bounded carefully

Useful patterns:

- allow self-skill use only for smoke testing / packaging verification / bounded validation
- avoid using the repository's own generated skill as primary evidence for authoring its contents
- record local wiring/layout in reports rather than pretending it is repo-tracked truth

---

## Local-Only Workspace Policy Guidance

When relevant, a repository-specific `AGENTS.md` may define local-only areas.

Typical examples:

- `.opencode/`
- local harnesses
- local fixtures
- local test projects
- local symlink-based skill wiring

Recommended rules:

- local-only areas remain gitignored unless explicitly intended otherwise
- do not commit private/local wiring unintentionally
- when local layout matters for the run, record it in reports
- distinguish local convenience structure from canonical tracked repository structure

---

## Reference vs Implementation Boundary Guidance

This is useful when a repository contains both inherited reference material and active implementation.

Recommended pattern:

- clearly mark read-only reference zones
- clearly mark append-only historical zones
- clearly mark active development zones
- clearly mark local-only zones

Possible policies:

- `Reference/` = read-only inspiration / provenance / preserved baseline
- `Reports/` = append-only history
- `docs/` = mutable working documentation
- active implementation directories = modifiable with intent
- local harness / local wiring = input evidence or dev-only material, not canonical source

This helps prevent accidental edits to historical or reference material.

---

## What Should Remain Repo-Specific

The following should usually be customized per repository rather than copied from another repo:

- project identity
- target technologies and domain
- specific source roots
- exact directory tree
- exact operation mode names
- exact required mode-specific report names
- exact validation checks
- exact reference directories
- exact skills/tools used by that repository
- exact human approval boundaries

The reporting discipline should stay highly consistent.
The domain details should stay repo-specific.

---

## Recommended Authoring Principle

When writing a repository-specific `AGENTS.md`, prefer a two-layer design:

### Layer 1: General Operating Rules
These can often be reused almost unchanged:

- evidence-first philosophy
- reporting rules
- `RUN_ID` discipline
- immutable history
- patch-forward correction
- execution style
- validation honesty
- skill/tool boundary rules

### Layer 2: Repository-Specific Rules
These must be customized:

- what the repository is for
- what counts as source-of-truth in that repo
- what modes exist in that repo
- what outputs are expected
- what directories exist
- what tools/skills are relevant
- what validations matter
- what mutation boundaries exist

This pattern keeps AGENTS files reusable without flattening away repo identity.

---

## Closing Guidance

A good repository-specific `AGENTS.md` should not merely describe the project.
It should define how an AI coding agent is expected to work inside that project:

- what to trust
- what to record
- what not to assume
- how to recover after interruption
- how to report honestly
- how to preserve history without rewriting it
- how to keep current truth converging through patch-forward correction

The best `AGENTS.md` files are not decorative.
They are operational documents for disciplined, resumable, evidence-based agent work.