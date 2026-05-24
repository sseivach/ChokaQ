---
layout: home

hero:
  name: "ChokaQ"
  text: "Фоновый обработчик заданий для .NET 10"
  tagline: "Background job engine с durable-хранилищем в SQL Server, типизированными заданиями, retry, изоляцией очередей, health checks и дашбордом The Deck."
  actions:
    - theme: brand
      text: Начать отсюда
      link: /ru/overview
    - theme: alt
      text: Запустить пример
      link: /samples/docker-compose
  image:
    src: /logo.png
    alt: Логотип ChokaQ

features:
  - icon: storage
    title: Durable-хранилище в SQL
    details: Принятая работа хранится в JobsHot. Завершенные задания уходят в Archive, а неуспешные - в DLQ для разбора оператором.
    link: /1-architecture/three-pillars
  - icon: code
    title: Типизированные задания
    details: Опишите DTO, напишите обработчик, зарегистрируйте type key и ставьте работу в очередь через IChokaQQueue.
    link: /job-contracts
  - icon: shield
    title: At-least-once execution
    details: ChokaQ сохраняет и повторяет принятую работу, но обработчики с внешними side effects все равно должны быть идемпотентными.
    link: /delivery-guarantees
  - icon: dashboard
    title: The Deck
    details: Real-time дашборд для активных заданий, очередей, DLQ, health-состояния, управления воркерами и recovery-сценариев.
    link: /4-the-deck/realtime-signalr
  - icon: settings
    title: Runtime policy
    details: Timeouts, retries, лимиты очередей, health thresholds, SQL-настройки и ограничения метрик задаются конфигурацией.
    link: /configuration
  - icon: book
    title: Подробная документация
    details: Документация объясняет настройку, runtime-поведение, delivery guarantees, операторские сценарии и архитектуру durable processing.
    link: /ru/overview
---

## Что такое ChokaQ

ChokaQ - это background job engine для .NET-приложений.

В обычном request/response-коде пользователь ждет, пока приложение выполнит
всю работу. Для быстрых операций это нормально. Для медленных или ненадежных
операций - отправки email, генерации отчетов, вызовов partner API, платежей или
обработки webhooks - это плохая граница.

ChokaQ позволяет request-пути сказать: "сохрани эту работу, выполни ее в фоне
и сделай состояние видимым".

Базовая модель простая:

1. Приложение ставит задание в очередь.
2. ChokaQ сохраняет задание.
3. Воркер забирает задание в работу.
4. Ваш обработчик выполняется.
5. ChokaQ фиксирует финальное состояние.
6. Оператор может посмотреть, что произошло, в The Deck.

## Ментальная модель

Удобно думать о ChokaQ как о трех совместно работающих частях:

| Часть | Простое объяснение | Почему это важно |
|---|---|---|
| Job storage | Durable-журнал принятой работы. | В SQL Server mode задания переживают restart процесса. |
| Workers | Фоновые исполнители, которые забирают и выполняют задания. | Работа выполняется вне request-пути. |
| The Deck | Операторское окно в систему. | Видны lag, ошибки, очереди, retries и строки DLQ. |

Самая важная таблица - `JobsHot`. В ней находится активная работа: задания,
которые ждут выполнения, уже забраны воркером или прямо сейчас обрабатываются.
Завершенные задания уходят из Hot, чтобы таблица активной работы оставалась
маленькой и быстрой.

## Что читать первым

Если вы впервые смотрите на ChokaQ, начните с [Обзора](/ru/overview). Он
объясняет систему небольшими шагами.

Если хотите сразу запустить код, начните с [Getting Started](/ru/getting-started)
или [Docker Compose Sample](/ru/samples/docker-compose). Эти страницы будут
переведены следующими партиями.

Если оцениваете надежность, прочитайте [Delivery Guarantees](/ru/delivery-guarantees)
до того, как писать обработчик, который отправляет email, списывает деньги или
вызывает внешнюю систему.

Если хотите понять архитектуру, дальше идут [Three Pillars](/ru/1-architecture/three-pillars),
[State Machine](/ru/2-lifecycle/state-machine) и [SQL Concurrency](/ru/3-deep-dives/sql-concurrency).

## Data flow

| Шаг | Что происходит | Смысл для человека |
|---|---|---|
| Enqueue | Задание вставляется в `JobsHot`. | Система приняла работу. |
| Fetch | Воркер забирает подходящую работу через SQL locking. | Теперь заданием владеет один воркер. |
| Processing | Обработчик выполняет пользовательский код. | Выполняется бизнес-операция. |
| Success | Строка переносится в Archive. | Задание завершилось штатно. |
| Retry | Строка остается в Hot с расписанием на будущее. | Повторить позже, не удерживая спящий поток. |
| DLQ | Строка переносится в Dead Letter Queue. | Нужен разбор человеком или repair workflow. |

::: tip Начните с малого
Сначала запустите пример, а потом читайте документацию, глядя на The Deck.
UI помогает быстрее понять архитектуру: вы видите, как задания переходят между
состояниями, а не только читаете об этом.
:::
