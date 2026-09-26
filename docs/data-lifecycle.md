# CMS data lifecycle

## Personal data CMS holds

CMS has no user table, comments or audit log, and keeps nothing keyed by
e-mail. What names a person:

| Where | What | Kept for |
|---|---|---|
| `collection_items.UpdatedBy` (NOT NULL), `collection_items.ArchivedBy` | Keycloak subject of the last editor and of the archiver | the life of the row |
| `content_blocks.UpdatedBy` (NOT NULL), `content_blocks.ArchivedBy` | Keycloak subject, or `deploy-pipeline` for manifest sync | the life of the row |
| Redis `draft:{clientId}:{sub}:{slug}` | an editor's unpublished page blocks | 48 hours |
| Redis `cd:item:…:{sub}`, `cd:vnew:…:{sub}`, `cd:new:…:{sub}` | an editor's unpublished collection item | 7 days |
| `collection_items.Data` of News: `author`, `body` | free text; the byline is usually a name | the life of the row |

Deleting a collection item or dropping a block from the manifest archives the
row (ADR-0042). Publishing a page hard-deletes that editor's Redis draft.

## Account erasure

`PUT /internal/v1/account-erasures/{request_id}` is core's Erasure command for
CMS (ADR-0051). Core's `docs/account-erasure-command.md` holds the contract;
this section is CMS's part of it.

- **Reach.** Internal Docker network only (ADR-0016). A request carrying a
  forwarding header Traefik adds (`X-Forwarded-*`, `Forwarded`, `X-Real-Ip`)
  gets a bare `404`, so the caller must not set one.
- **Token.** The usual JwtBearer check (`aud` = `skycms`), then the
  `AccountErasure` policy: `azp` = `core-erasure` and the role
  `cms:account:erase` under `resource_access.skycms.roles`. Roles under
  `resource_access[azp]`, which `cms:access` uses, do not count here. The
  caller's own access marker is checked as on every route.
- **Subject must be blocked.** The subject's marker must already be in
  account-access Redis. Missing: `409 subject_not_blocked`. Gate `off` or
  Redis unreachable: `503 subject_block_unverifiable`.
- **What it erases.**
  1. The subject's drafts, found by `SCAN MATCH draft:*:{sub}:*` and
     `cd:*:{sub}`. This runs before the transaction and repeating it deletes
     nothing.
  2. In one transaction, under an advisory lock on `request_id`: every
     `UpdatedBy` and `ArchivedBy` equal to the subject in both tables,
     archived rows included, becomes Silinmiş kullanıcı
     (`00000000-0000-4000-8000-000000000000`). `UpdatedAt` and `Version` do
     not change. The receipt is written in the same transaction.
- **Answer.** `200 {"request_id","status":"completed","completed_at","counts"}`
  with `counts` = `actor_columns_replaced`, `drafts_deleted`. A repeat, or a
  concurrent duplicate, returns the stored body without doing the work again.
  Other answers: `400 invalid_erasure_command`, `401` (token rejected),
  `403 erasure_forbidden`, `404` (ingress), `409 subject_not_blocked`,
  `503` with `Retry-After` (`subject_block_unverifiable`,
  `erasure_store_unavailable`).
- **Logs.** The body is never logged. Log lines carry the request id and a
  fixed code, never the subject or an address. The addresses in the command
  are validated and dropped: nothing in CMS is keyed by e-mail.
- **Proof.** `account_erasure_receipts` (`request_id`, `completed_at`,
  `counts`) holds no subject and no address. It is the deletion record and is
  kept at least three years; no code path deletes it.

## Why published News keeps its byline

A News item's `author` and `body` are the club's editorial record, like the
recipient name on an issued Certificate. Erasure clears the subjects behind
the item (who edited or archived it) and leaves the published text as it is
(ADR-0051, decision 5). If the person explicitly asks for the byline to go,
the balancing test is done by hand and its outcome recorded.
