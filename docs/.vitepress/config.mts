import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'RhinoDock',
  description: 'A small, extensible C# workspace inside Rhino 8.',
  base: '/RhinoDock/',
  cleanUrls: true,
  themeConfig: {
    nav: [
      { text: 'Guide', link: '/getting-started' },
      { text: 'Changelog', link: 'https://github.com/Daxler-design/RhinoDock/blob/main/CHANGELOG.md' },
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/Daxler-design/RhinoDock' }],
    search: { provider: 'local' },
    sidebar: [
      { text: 'Start', items: [{ text: 'Getting started', link: '/getting-started' }] },
      {
        text: 'Use',
        items: [
          { text: 'User guide', link: '/user-guide' },
          { text: 'Writing Scripts', link: '/scripts' },
          { text: 'Agent tab', link: '/agent' },
          { text: 'Extensions', link: '/extensions' },
          { text: 'Debugging and the Mirror', link: '/debugging' },
        ],
      },
      { text: 'Reference', items: [{ text: 'Known limitations', link: '/limitations' }] },
      {
        text: 'Contribute',
        items: [
          { text: 'Development', link: '/development' },
          { text: 'Architecture', link: '/architecture' },
        ],
      },
    ],
  },
})
