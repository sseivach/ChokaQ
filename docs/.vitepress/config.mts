import { defineConfig } from 'vitepress'

const base = process.env.VITEPRESS_BASE ?? '/'

const enNav = [
  { text: 'Overview', link: '/overview' },
  { text: 'Try ChokaQ', link: '/try-chokaq' },
  { text: 'Getting Started', link: '/getting-started' },
  { text: 'Architecture', link: '/1-architecture/three-pillars' },
  { text: 'Deep Dives', link: '/3-deep-dives/sql-concurrency' },
  { text: 'The Deck', link: '/4-the-deck/realtime-signalr' },
  { text: 'Operations', link: '/5-operations/slo-alerts' }
]

const enSidebar = [
  {
    text: 'Introduction',
    items: [
      { text: 'Overview', link: '/overview' },
      { text: 'Runtime Model', link: '/runtime-model' },
      { text: 'Try ChokaQ', link: '/try-chokaq' },
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'API Examples', link: '/api-examples' },
      { text: 'Architecture Decisions', link: '/architecture-decisions' },
      { text: 'Architecture Q&A', link: '/architecture-questions' },
      { text: 'Job Contracts', link: '/job-contracts' },
      { text: 'Docker Compose Sample', link: '/samples/docker-compose' },
      { text: 'Local NuGet Lab', link: '/samples/nuget-lab' },
      { text: 'Delivery Guarantees', link: '/delivery-guarantees' },
      { text: 'Runtime Configuration', link: '/configuration' },
      { text: '0.1.0-preview.1 Notes', link: '/release-notes/0.1.0-preview.1' },
      { text: 'Why ChokaQ?', link: '/why-chokaq' }
    ]
  },
  {
    text: 'Architecture',
    collapsed: false,
    items: [
      { text: 'Three Pillars', link: '/1-architecture/three-pillars' },
      { text: 'Package Topology', link: '/1-architecture/package-topology' },
      { text: 'Bus Vs Pipe Dispatch', link: '/1-architecture/bus-vs-pipe' },
      { text: 'Queue Registry And Profiles', link: '/1-architecture/queue-registry-and-profiles' },
      { text: 'Why SQL Server?', link: '/1-architecture/why-sql-server' },
      { text: 'Minimal Dependencies', link: '/1-architecture/minimal-dependencies' },
      { text: 'Smart Worker (Fast-Fail)', link: '/1-architecture/smart-worker' }
    ]
  },
  {
    text: 'Job Lifecycle',
    collapsed: false,
    items: [
      { text: 'State Machine', link: '/2-lifecycle/state-machine' },
      { text: 'Retry And DLQ', link: '/2-lifecycle/retry-and-dlq' },
      { text: 'Dead Letter Queue', link: '/2-lifecycle/dead-letter-queue' },
      { text: 'Heartbeat', link: '/2-lifecycle/heartbeat' },
      { text: 'Bulkhead Isolation', link: '/2-lifecycle/bulkhead-isolation' },
      { text: 'Zombie Rescue', link: '/2-lifecycle/zombie-rescue' },
      { text: 'Graceful Shutdown', link: '/2-lifecycle/graceful-shutdown' },
      { text: 'Job Context And Cancellation', link: '/2-lifecycle/job-context-and-cancellation' },
      { text: 'Failure Taxonomy', link: '/2-lifecycle/failure-taxonomy' }
    ]
  },
  {
    text: 'Deep Dives',
    collapsed: false,
    items: [
      { text: 'SQL Concurrency (UPDLOCK)', link: '/3-deep-dives/sql-concurrency' },
      { text: 'SQL Schema Atlas', link: '/3-deep-dives/sql-schema-atlas' },
      { text: 'SQL Query Reference', link: '/3-deep-dives/sql-query-reference' },
      { text: 'Transaction Integrity', link: '/3-deep-dives/transaction-integrity' },
      { text: 'Backpressure Policy', link: '/3-deep-dives/backpressure-policy' },
      { text: 'Prefetching', link: '/3-deep-dives/prefetching' },
      { text: 'Bounded Prefetch', link: '/3-deep-dives/bounded-prefetch' },
      { text: 'System.Threading.Channels', link: '/3-deep-dives/system-threading-channels' },
      { text: 'Serialization And Envelope Limits', link: '/3-deep-dives/serialization-and-envelope-limits' },
      { text: 'Middleware Pipeline', link: '/3-deep-dives/middleware-pipeline' },
      { text: 'SQL Transient Retry Policy', link: '/3-deep-dives/sql-transient-retry-policy' },
      { text: 'Expression Trees', link: '/3-deep-dives/expression-trees' },
      { text: 'Dynamic Concurrency Limiter', link: '/3-deep-dives/dynamic-concurrency-limiter' },
      { text: 'Idempotency Middleware', link: '/3-deep-dives/idempotency-middleware' },
      { text: 'Telemetry', link: '/3-deep-dives/telemetry' },
      { text: 'Metrics', link: '/3-deep-dives/metrics' },
      { text: 'Type Resolution', link: '/3-deep-dives/type-resolution' },
      { text: 'Deduplication Layer', link: '/3-deep-dives/deduplication-layer' },
      { text: 'Alternatives Analysis', link: '/3-deep-dives/alternatives-analysis' },
      { text: 'Failure Modes', link: '/3-deep-dives/failure-modes' },
      { text: 'In-Memory Engine', link: '/3-deep-dives/memory-management' }
    ]
  },
  {
    text: 'The Deck (Dashboard)',
    collapsed: false,
    items: [
      { text: 'Panel Guide', link: '/4-the-deck/panel-guide' },
      { text: 'Queue Controls', link: '/4-the-deck/queue-controls' },
      { text: 'Circuit Breakers', link: '/4-the-deck/circuit-breakers' },
      { text: 'SignalR Notification Contract', link: '/4-the-deck/signalr-notification-contract' },
      { text: 'Authorization Model', link: '/4-the-deck/authorization-model' },
      { text: 'Destructive Actions', link: '/4-the-deck/destructive-actions' },
      { text: 'Paging, Sorting And Filtering', link: '/4-the-deck/paging-sorting-filtering' },
      { text: 'Real-time SignalR', link: '/4-the-deck/realtime-signalr' },
      { text: 'Rolling Observability', link: '/4-the-deck/rolling-observability' },
      { text: 'Edit + Resurrect', link: '/4-the-deck/resurrect-dlq' }
    ]
  },
  {
    text: 'Operations',
    collapsed: false,
    items: [
      { text: 'Production Readiness', link: '/5-operations/production-readiness-checklist' },
      { text: 'Health Checks', link: '/5-operations/health-checks' },
      { text: 'Schema Bootstrap And Migrations', link: '/5-operations/schema-bootstrap-and-migrations' },
      { text: 'Retention Cleanup', link: '/5-operations/retention-cleanup' },
      { text: 'SLOs And Alerts', link: '/5-operations/slo-alerts' },
      { text: 'Operations Runbooks', link: '/5-operations/runbooks' },
      { text: 'Heartbeat Pressure', link: '/5-operations/heartbeat-pressure' },
      { text: 'Idempotent Handlers', link: '/5-operations/idempotent-handlers' },
      { text: 'Type-Key Troubleshooting', link: '/5-operations/type-key-troubleshooting' },
      { text: 'Scaling Model', link: '/5-operations/scaling-model' },
      { text: 'Worker Autoscaling', link: '/5-operations/autoscaling' }
    ]
  }
]

const ruNav = [
  { text: 'Обзор', link: '/ru/overview' },
  { text: 'Быстрый старт', link: '/ru/getting-started' },
  { text: 'API', link: '/ru/api-examples' },
  { text: 'Architecture', link: '/ru/1-architecture/three-pillars' },
  { text: 'Job Lifecycle', link: '/ru/2-lifecycle/state-machine' },
  { text: 'Deep Dives', link: '/ru/3-deep-dives/sql-concurrency' },
  { text: 'The Deck', link: '/ru/4-the-deck/realtime-signalr' },
  { text: 'Operations', link: '/ru/5-operations/slo-alerts' }
]

const ruSidebar = [
  {
    text: 'Введение',
    items: [
      { text: 'Обзор', link: '/ru/overview' },
      { text: 'Runtime Model', link: '/ru/runtime-model' },
      { text: 'Быстрый старт', link: '/ru/getting-started' },
      { text: 'Примеры API', link: '/ru/api-examples' },
      { text: 'Job Contracts', link: '/ru/job-contracts' },
      { text: 'Docker Compose Sample', link: '/ru/samples/docker-compose' },
      { text: 'Local NuGet Lab', link: '/ru/samples/nuget-lab' },
      { text: 'Delivery Guarantees', link: '/ru/delivery-guarantees' },
      { text: 'Runtime Configuration', link: '/ru/configuration' },
      { text: '0.1.0-preview.1 Notes', link: '/ru/release-notes/0.1.0-preview.1' },
      { text: 'Почему ChokaQ?', link: '/ru/why-chokaq' }
    ]
  },
  {
    text: 'Architecture',
    collapsed: false,
    items: [
      { text: 'Three Pillars', link: '/ru/1-architecture/three-pillars' },
      { text: 'Package Topology', link: '/ru/1-architecture/package-topology' },
      { text: 'Bus Vs Pipe Dispatch', link: '/ru/1-architecture/bus-vs-pipe' },
      { text: 'Queue Registry And Profiles', link: '/ru/1-architecture/queue-registry-and-profiles' },
      { text: 'Почему SQL Server?', link: '/ru/1-architecture/why-sql-server' },
      { text: 'Minimal Dependencies', link: '/ru/1-architecture/minimal-dependencies' },
      { text: 'Smart Worker (Fast-Fail)', link: '/ru/1-architecture/smart-worker' },
      { text: 'Architecture Decisions', link: '/ru/architecture-decisions' },
      { text: 'Architecture Q&A', link: '/ru/architecture-questions' }
    ]
  },
  {
    text: 'Job Lifecycle',
    collapsed: false,
    items: [
      { text: 'Машина состояний', link: '/ru/2-lifecycle/state-machine' },
      { text: 'Retry And DLQ', link: '/ru/2-lifecycle/retry-and-dlq' },
      { text: 'Dead Letter Queue', link: '/ru/2-lifecycle/dead-letter-queue' },
      { text: 'Heartbeat', link: '/ru/2-lifecycle/heartbeat' },
      { text: 'Bulkhead Isolation', link: '/ru/2-lifecycle/bulkhead-isolation' },
      { text: 'Zombie Rescue', link: '/ru/2-lifecycle/zombie-rescue' },
      { text: 'Graceful Shutdown', link: '/ru/2-lifecycle/graceful-shutdown' },
      { text: 'Job Context And Cancellation', link: '/ru/2-lifecycle/job-context-and-cancellation' },
      { text: 'Failure Taxonomy', link: '/ru/2-lifecycle/failure-taxonomy' }
    ]
  },
  {
    text: 'Deep Dives',
    collapsed: false,
    items: [
      { text: 'SQL Concurrency (UPDLOCK)', link: '/ru/3-deep-dives/sql-concurrency' },
      { text: 'SQL Schema Atlas', link: '/ru/3-deep-dives/sql-schema-atlas' },
      { text: 'SQL Query Reference', link: '/ru/3-deep-dives/sql-query-reference' },
      { text: 'Transaction Integrity', link: '/ru/3-deep-dives/transaction-integrity' },
      { text: 'SQL Transient Retry Policy', link: '/ru/3-deep-dives/sql-transient-retry-policy' },
      { text: 'Backpressure Policy', link: '/ru/3-deep-dives/backpressure-policy' },
      { text: 'Dynamic Concurrency Limiter', link: '/ru/3-deep-dives/dynamic-concurrency-limiter' },
      { text: 'Prefetching', link: '/ru/3-deep-dives/prefetching' },
      { text: 'Bounded Prefetch', link: '/ru/3-deep-dives/bounded-prefetch' },
      { text: 'System.Threading.Channels', link: '/ru/3-deep-dives/system-threading-channels' },
      { text: 'Middleware Pipeline', link: '/ru/3-deep-dives/middleware-pipeline' },
      { text: 'Expression Trees', link: '/ru/3-deep-dives/expression-trees' },
      { text: 'Type Resolution', link: '/ru/3-deep-dives/type-resolution' },
      { text: 'Serialization And Envelope Limits', link: '/ru/3-deep-dives/serialization-and-envelope-limits' },
      { text: 'Idempotency Middleware', link: '/ru/3-deep-dives/idempotency-middleware' },
      { text: 'Telemetry', link: '/ru/3-deep-dives/telemetry' },
      { text: 'Metrics', link: '/ru/3-deep-dives/metrics' },
      { text: 'Deduplication Layer', link: '/ru/3-deep-dives/deduplication-layer' },
      { text: 'Failure Modes', link: '/ru/3-deep-dives/failure-modes' },
      { text: 'Alternatives Analysis', link: '/ru/3-deep-dives/alternatives-analysis' },
      { text: 'In-Memory Engine', link: '/ru/3-deep-dives/memory-management' }
    ]
  },
  {
    text: 'The Deck',
    collapsed: false,
    items: [
      { text: 'Panel Guide', link: '/ru/4-the-deck/panel-guide' },
      { text: 'Real-time SignalR', link: '/ru/4-the-deck/realtime-signalr' },
      { text: 'Queue Controls', link: '/ru/4-the-deck/queue-controls' },
      { text: 'Circuit Breakers', link: '/ru/4-the-deck/circuit-breakers' },
      { text: 'SignalR Notification Contract', link: '/ru/4-the-deck/signalr-notification-contract' },
      { text: 'Authorization Model', link: '/ru/4-the-deck/authorization-model' },
      { text: 'Destructive Actions', link: '/ru/4-the-deck/destructive-actions' },
      { text: 'Paging, Sorting And Filtering', link: '/ru/4-the-deck/paging-sorting-filtering' },
      { text: 'Rolling Observability', link: '/ru/4-the-deck/rolling-observability' },
      { text: 'Edit + Resurrect', link: '/ru/4-the-deck/resurrect-dlq' }
    ]
  },
  {
    text: 'Operations',
    collapsed: false,
    items: [
      { text: 'Production Readiness', link: '/ru/5-operations/production-readiness-checklist' },
      { text: 'Health Checks', link: '/ru/5-operations/health-checks' },
      { text: 'Schema Bootstrap And Migrations', link: '/ru/5-operations/schema-bootstrap-and-migrations' },
      { text: 'Retention Cleanup', link: '/ru/5-operations/retention-cleanup' },
      { text: 'SLOs And Alerts', link: '/ru/5-operations/slo-alerts' },
      { text: 'Operations Runbooks', link: '/ru/5-operations/runbooks' },
      { text: 'Heartbeat Pressure', link: '/ru/5-operations/heartbeat-pressure' },
      { text: 'Idempotent Handlers', link: '/ru/5-operations/idempotent-handlers' },
      { text: 'Type-Key Troubleshooting', link: '/ru/5-operations/type-key-troubleshooting' },
      { text: 'Scaling Model', link: '/ru/5-operations/scaling-model' },
      { text: 'Worker Autoscaling', link: '/ru/5-operations/autoscaling' }
    ]
  }
]

const commonThemeConfig = {
  logo: '/logo.png',
  siteTitle: 'ChokaQ',
  i18nRouting: true,

  socialLinks: [
    { icon: 'github', link: 'https://github.com/sseivach/ChokaQ' }
  ],

  search: {
    provider: 'local'
  }
}

export default defineConfig({
  title: 'ChokaQ',
  description: 'SQL-backed background job engine with full architecture documentation for .NET 10',
  base,

  head: [
    ['link', { rel: 'icon', href: `${base}logo.png` }],
    ['meta', { name: 'theme-color', content: '#4589ff' }],
    ['meta', { property: 'og:title', content: 'ChokaQ - .NET 10 Background Job Engine' }],
    ['meta', { property: 'og:description', content: 'SQL-backed background job processor with The Deck dashboard and architecture documentation for production patterns.' }]
  ],

  appearance: 'dark',

  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      link: '/',
      title: 'ChokaQ',
      description: 'SQL-backed background job engine with full architecture documentation for .NET 10',
      themeConfig: {
        ...commonThemeConfig,
        nav: enNav,
        sidebar: enSidebar,
        outline: {
          level: [2, 3],
          label: 'On this page'
        },
        footer: {
          message: 'Apache 2.0 Licensed',
          copyright: '(c) 2026 Sergei Seivach'
        }
      }
    },
    ru: {
      label: 'Русский',
      lang: 'ru-RU',
      link: '/ru/',
      title: 'ChokaQ',
      description: 'Фоновый обработчик заданий для .NET 10 с durable-хранилищем в SQL Server',
      themeConfig: {
        ...commonThemeConfig,
        nav: ruNav,
        sidebar: ruSidebar,
        outline: {
          level: [2, 3],
          label: 'На этой странице'
        },
        footer: {
          message: 'Лицензия Apache 2.0',
          copyright: '(c) 2026 Sergei Seivach'
        }
      }
    }
  }
})
