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
| Verification | Running the pipeline classes (envelope, policy checks, encryption, parameter validation, URL and header building, forwarding, timeouts, cancellation, size limits, bulkhead) against a recording upstream in the AI tool's sandbox. This run exposed the decrypted-buffer defect listed in DEFECT-REPORT.md. |

## My own work

<!-- To be completed by the candidate before submitting. Keep only what is true. -->

- [ ] Reviewed all generated code and documents; I can explain and modify every part.
- [ ] Built the solution and ran the full xUnit suite locally (`dotnet test`).
- [ ] Ran the API against SQL Server and exercised it with the commands in README.md.
- [ ] Changes I made after review: _..._

## Note on verification

The AI tool's sandbox had no access to NuGet, so the complete solution (EF Core, SQL Server
provider, xUnit) could not be restored or built there. The EF Core code, the admin endpoints and
the xUnit suite must be compiled and run locally before submission (see the checklist above).
