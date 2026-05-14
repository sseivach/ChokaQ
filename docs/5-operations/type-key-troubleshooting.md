# Type-Key Troubleshooting

ChokaQ stores the job type as a persisted message-contract key. In production,
register every Bus job through a profile and use stable keys such as
`email.send.v1`.

## Symptoms

| Symptom | Likely cause |
|---|---|
| Restart from DLQ logs unknown type key | The stored key is not registered and is not an assembly-qualified fallback. |
| A renamed CLR class no longer requeues old jobs | The old rows used CLR identity instead of a stable profile key. |
| A changed payload fails deserialization | The DTO change was breaking but reused the same type key. |
| In-memory stale channel item is skipped | The Hot row type or payload changed after the channel item was buffered. |

## Checks

1. Confirm `TypeResolution.RequireRegisteredJobTypes = true` in production.
2. Verify the job profile registers the stored key:

```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, SendEmailHandler>("email.send.v1");
    }
}
```

3. Inspect the row's persisted `Type` value in Hot/DLQ.
4. If the payload schema changed incompatibly, create a new key such as
   `email.send.v2` and keep old handlers or migrate old rows.

## Response

- For unknown DLQ rows, register the old key temporarily or migrate the row to a
  supported key/payload pair.
- Do not resolve by CLR short name. Short names can collide across namespaces
  and are intentionally not scanned at requeue time.
- For broken payloads, edit and resurrect only after validating that the handler
  remains idempotent.
