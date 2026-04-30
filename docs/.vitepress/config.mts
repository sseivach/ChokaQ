import { defineConfig } from 'vitepress'

export default defineConfig({
  title: "ChokaQ",
  description: "Zero-Dependency Background Job Engine for .NET 10",
  
  head: [
    ['link', { rel: 'icon', href: '/logo.png' }],
    ['meta', { name: 'theme-color', content: '#4589ff' }],
    ['meta', { property: 'og:title', content: 'ChokaQ — Zero-Dependency Job Engine' }],
    ['meta', { property: 'og:description', content: 'Enterprise-grade background job framework with Three Pillars architecture, Expression Trees, and zero third-party dependencies.' }],
  ],
  
  appearance: 'dark',

  themeConfig: {
    logo: '/logo.png',
    siteTitle: 'ChokaQ',

    nav: [
      { text: 'Getting Started', link: '/getting-started' },
      { text: 'Architecture', link: '/1-architecture/three-pillars' },
      { text: 'Deep Dives', link: '/3-deep-dives/sql-concurrency' },
      { text: 'The Deck', link: '/4-the-deck/realtime-signalr' }
    ],

    sidebar: [
      {
        text: '🚀 Introduction',
        items: [
          { text: 'Getting Started', link: '/getting-started' },
          { text: 'Why ChokaQ?', link: '/why-chokaq' }
        ]
      },
      {
        text: '🏗️ Architecture',
        collapsed: false,
        items: [
          { text: 'Three Pillars', link: '/1-architecture/three-pillars' },
          { text: 'Why SQL Server?', link: '/1-architecture/why-sql-server' },
          { text: 'Zero-Dependency', link: '/1-architecture/zero-dependency' },
          { text: 'Smart Worker (Fast-Fail)', link: '/1-architecture/smart-worker' }
        ]
      },
      {
        text: '🔄 Job Lifecycle',
        collapsed: false,
        items: [
          { text: 'State Machine', link: '/2-lifecycle/state-machine' },
          { text: 'Bulkhead Isolation', link: '/2-lifecycle/bulkhead-isolation' },
          { text: 'Zombie Rescue', link: '/2-lifecycle/zombie-rescue' }
        ]
      },
      {
        text: '🔬 Deep Dives',
        collapsed: false,
        items: [
          { text: 'SQL Concurrency (UPDLOCK)', link: '/3-deep-dives/sql-concurrency' },
          { text: 'Expression Trees', link: '/3-deep-dives/expression-trees' },
          { text: 'Elastic Semaphore', link: '/3-deep-dives/elastic-semaphore' },
          { text: 'In-Memory Engine', link: '/3-deep-dives/memory-management' }
        ]
      },
      {
        text: '🖥️ The Deck (Dashboard)',
        collapsed: false,
        items: [
          { text: 'Real-time SignalR', link: '/4-the-deck/realtime-signalr' },
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
      copyright: '© 2026 Sergei Seivach'
    }
  }
})