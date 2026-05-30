import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'dashboard',
      component: () => import('@/pages/DashboardPage.vue'),
    },
    {
      path: '/source/:id',
      name: 'source',
      component: () => import('@/pages/SourcePage.vue'),
      props: true,
    },
    {
      path: '/query',
      name: 'query',
      component: () => import('@/pages/QueryPage.vue'),
    },
    {
      path: '/archives',
      name: 'archives',
      component: () => import('@/pages/ArchivesPage.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/pages/SettingsPage.vue'),
    },
  ],
})

export default router
