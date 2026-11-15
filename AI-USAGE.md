# AI usage declaration

## Tools

- **Claude (Anthropic)**, used through the Claude app in an agentic coding session.

## What it was used for

| Area | Use |
|------|-----|
| Requirements | Reading the assignment, listing gaps and ambiguities, proposing the assumptions recorded in DESIGN-NOTES.md section 1. |
| Implementation | Writing the first version of the solution: entities and `DbContext`, the request pipeline, authentication, AES-GCM handling, audit, admin endpoints. |
| Tests | Writing the xUnit tests and the recording mock destination. |
| Documentation | Drafting README.md, DESIGN-NOTES.md and DEFECT-REPORT.md. |
| Code review | Reviewing the finished solution, and fixing the findings listed in DEFECT-REPORT.md section 1 together with tests for them. |
| Later improvements | EF Core migrations, the configuration API with validation, the circuit breaker, Swagger, and the setup scripts that keep development secrets out of the repository. |
| CI | Writing the GitHub Actions workflow and the SQL Server smoke-test script. |
| Verification | Running the pipeline classes (envelope, policy checks, encryption, parameter validation, URL and header building, forwarding, timeouts, cancellation, size limits, bulkhead) against a recording upstream in the AI tool's sandbox. This run exposed the decrypted-buffer defect listed in DEFECT-REPORT.md. |

## My own work

- Built the solution and ran the xUnit suite myself in a GitHub Codespaces dev container
  (.NET SDK 8.0.428). At that point the suite had 83 tests; all passed.
- Ran the API against SQL Server 2022 in that environment and exercised it by hand: a forwarded
  request, a rejected request, an encrypted operation, and the audit rows in `RequestLogs`.
- Ran the end-to-end smoke test there (19 checks at that point, all passed), including the
  `rowversion` conflict (409).
- Reviewed all generated code and documents; I can explain and modify every part.
- I remain responsible for the complete submitted solution.

Tests and checks were added after those runs (code review fixes, configuration API, circuit
breaker, migrations). The current totals are the ones reported by the GitHub Actions workflow for
the latest commit.

## Note on verification

The AI tool's sandbox had no access to NuGet, so the complete solution could not be built there.
It is verified by the GitHub Actions workflow in `.github/workflows/ci.yml`, which the AI tool also
wrote: the full xUnit suite; a check that the migrations match the model; an end-to-end run of the
API against SQL Server 2022 (`scripts/smoke-test.sh`), once with the schema applied by the
migrations and once from `database/schema.sql`; the local setup described in README.md; and the
Windows setup script.
