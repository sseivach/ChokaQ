# Circuit Breakers

![Circuit breaker state flow](/diagrams/31-circuit-breaker-state-flow.png)

![The Deck circuit breakers panel](/screenshots/the-deck/circuit-breakers.png)

Circuit breakers защищают систему от повторения known-bad action.

Retry отвечает: "Должно ли это individual job попробовать еще раз позже?"  
Circuit breaker отвечает: "Должен ли этот job type вообще выполняться сейчас?"

## States

| State | Meaning |
|---|---|
| Closed | Execution разрешен. Failures считаются внутри policy window. |
| Open | Execution заблокирован. Jobs reschedule'ятся вместо вызова handler. |
| Half-open | Limited probe разрешен для проверки recovery. |

## Execution flow

1. Processor получает fetched job.
2. Запрашивает у circuit breaker execution permit по job type.
3. Если permit получен, execution продолжается к `MarkAsProcessing`.
4. Success reports recovery to breaker.
5. Failure reports severity to breaker.
6. Если circuit opens, последующие jobs delay'ятся с `CircuitBreakerDelay`.

Circuit check происходит перед user code, чтобы не hammer'ить failing downstream dependency.

## Что The Deck должен показывать

The Deck должен делать circuit state видимым:

- circuit key;
- current state;
- failure count;
- last failure time;
- half-open probe status;
- next retry/probe time;
- operator action history.

## Operator guidance

Не force-close breaker только потому, что queue растет. Сначала проверьте, что downstream dependency, credentials, DNS, schema или handler bug исправлены. Closing breaker против все еще broken dependency превращает protection обратно в retry storm.

## Архитектурное решение

### Почему этот pattern?

Retries защищают individual jobs от temporary failures. Circuit breakers защищают всю систему от systemic failures.

### Trade-offs

Circuit breaker может задержать valid work, если он opens too aggressively. Если он слишком permissive, он не защищает downstream systems. Policy tuning должен основываться на failure rate, failure severity и recovery behavior.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Retry only | Проще. | Может overwhelm broken dependency. |
| Global pause | Strong protection. | Останавливает unrelated job types. |
| Downstream rate limit only | Delegates protection. | Workers все еще могут тратить capacity на calls, которые dependency уже rejects. |

### Дополнительные вопросы

**Почему не только retries?**  
Retries реагируют per job. Circuit breaker реагирует на systemic failure и останавливает new executions до попадания в broken dependency.

**Зачем half-open?**  
Он дает controlled recovery probe без unleashing full backlog.

**Что должно происходить, когда breaker open?**  
Jobs должны reschedule'иться или hold'иться, а не выполняться и fail'иться снова и снова.
