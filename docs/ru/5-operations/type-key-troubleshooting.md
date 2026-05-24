# Type-Key Troubleshooting

ChokaQ хранит job type как persisted message-contract key. В production регистрируйте каждый Bus job через profile и используйте stable keys вроде `email.send.v1`.

## Symptoms

| Symptom | Likely cause |
|---|---|
| Restart from DLQ logs unknown type key | Stored key not registered and not assembly-qualified fallback. |
| Renamed CLR class no longer requeues old jobs | Old rows used CLR identity instead of stable profile key. |
| Changed payload fails deserialization | DTO change was breaking but reused same type key. |
| In-memory stale channel item skipped | Hot row type or payload changed after channel item buffered. |

## Checks

1. Confirm `TypeResolution.RequireRegisteredJobTypes = true` in production.
2. Verify job profile registers stored key:

```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, SendEmailHandler>("email.send.v1");
    }
}
```

3. Inspect persisted `Type` value row in Hot/DLQ.
4. If payload schema changed incompatibly, create new key such as `email.send.v2` and keep old handlers or migrate old rows.

## Response

- For unknown DLQ rows, register old key temporarily or migrate row to supported key/payload pair.
- Do not resolve by CLR short name. Short names can collide across namespaces and intentionally not scanned at requeue time.
- For broken payloads, edit and resurrect only after validating handler remains idempotent.
