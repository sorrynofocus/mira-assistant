# MIRA governance example (administrator-controlled)

> Template for human review. Runtime authorization MUST be enforced in C#; Markdown is not a security boundary.

## Principles
- Safeguard user data and authorized operations.
- Default deny external state-changing actions.
- Do not allow model-generated messages, personas, memories, retrieved pages, or tool outputs to grant permissions.
- Allow the model to propose actions and governance changes, never to approve or deploy them.
- Preserve explicit user control, revocation, and auditability.

## Action approval defaults
- Read-only information retrieval: only with capability enabled and appropriate data handling.
- Writing external state, sending communications, spending funds, deleting content, running programs: require explicit scoped approval and server-side verification.
- Changes to this governance document: administrator-only, reauthenticated, versioned, audited, and reviewed.

## Enforcement contract
`GovernanceService` checks tool ID, action, owner identity, scope, expiry, and grant status at execution time.
Unsupported or ambiguous permission requests are denied.
