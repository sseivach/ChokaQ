import { defineConfig } from 'vitepress'

export default defineConfig({
  title: "ChokaQ",
  description: "SQL-backed background job engine and architecture learning project for .NET 10",
  
  head: [
    ['link', { rel: 'icon', href: '/logo.png' }],
    ['meta', { name: 'theme-color', content: '#4589ff' }],
    ['meta', { property: 'og:title', content: 'ChokaQ - .NET 10 Background Job Engine' }],
    ['meta', { property: 'og:description', content: 'SQL-backed background job processor with The Deck dashboard and architecture documentation for production patterns.' }],
  ],
  
  appearance: 'dark',

  themeConfig: {
    logo: '/logo.png',
    siteTitle: 'ChokaQ',

    nav: [
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'Learning Track', link: '/learning-track' },
      { text: 'Architecture', link: '/1-architecture/three-pillars' },
      { text: 'Deep Dives', link: '/3-deep-dives/sql-concurrency' },
      { text: 'The Deck', link: '/4-the-deck/realtime-signalr' }
    ],

    sidebar: [
      {
        text: 'Introduction',
        items: [
          { text: 'Getting Started', link: '/getting-started' },
          { text: 'Docker Compose Sample', link: '/samples/docker-compose' },
          { text: 'Runtime Configuration', link: '/configuration' },
          { text: 'Release Strategy', link: '/release-strategy' },
          { text: 'Release Checklist', link: '/release-checklist' },
          { text: 'Learning Track', link: '/learning-track' },
          { text: 'Why ChokaQ?', link: '/why-chokaq' }
        ]
      },
      {
        text: 'Architecture',
        collapsed: false,
        items: [
          { text: 'Three Pillars', link: '/1-architecture/three-pillars' },
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
          { text: 'Bulkhead Isolation', link: '/2-lifecycle/bulkhead-isolation' },
          { text: 'Zombie Rescue', link: '/2-lifecycle/zombie-rescue' }
        ]
      },
      {
        text: 'Deep Dives',
        collapsed: false,
        items: [
          { text: 'SQL Concurrency (UPDLOCK)', link: '/3-deep-dives/sql-concurrency' },
          { text: 'Backpressure Policy', link: '/3-deep-dives/backpressure-policy' },
          { text: 'Expression Trees', link: '/3-deep-dives/expression-trees' },
          { text: 'Dynamic Concurrency Limiter', link: '/3-deep-dives/dynamic-concurrency-limiter' },
          { text: 'In-Memory Engine', link: '/3-deep-dives/memory-management' }
        ]
      },
      {
        text: 'The Deck (Dashboard)',
        collapsed: false,
        items: [
          { text: 'Real-time SignalR', link: '/4-the-deck/realtime-signalr' },
          { text: 'Rolling Observability', link: '/4-the-deck/rolling-observability' },
          { text: 'Edit + Resurrect', link: '/4-the-deck/resurrect-dlq' }
        ]
      }
    ],

    socialLinks: [
      { icon: 'github', link: 'https://github.com/sseivach/ChokaQ' }
    ],

    search: {
      provider: 'local'
    },

    outline: {
      level: [2, 3],
      label: 'On this page'
    },

    footer: {
      message: 'Apache 2.0 Licensed',
      copyright: '(c) 2026 Sergei Seivach'
    }
  }
})
