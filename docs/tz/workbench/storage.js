// Совместимость восстановленной страницы с файловым хранилищем рабочей копии.
(() => {
  let records = [];
  const watchers = new Map();
  const owner = 'u_MRkqqOq1sCTkRzHAlYDjdg';
  let loading = false;
  let previous = '';

  async function refresh() {
    if (loading) return;
    loading = true;
    try {
      const response = await fetch('/api/comments');
      if (!response.ok) throw new Error('Не удалось прочитать обсуждение');
      const next = await response.json();
      const serialized = JSON.stringify(next);
      records = next;
      if (previous !== serialized) {
        previous = serialized;
        watchers.forEach((listeners, path) => {
          const docs = records.filter(r => r.collection === path)
            .map(r => ({ id: r.id, data: () => r.data }));
          listeners.forEach(listener => listener.success({ docs }));
        });
      }
    } catch (error) {
      watchers.forEach(listeners => listeners.forEach(listener => listener.failure(error)));
    } finally {
      loading = false;
    }
  }

  async function write(body) {
    const response = await fetch('/api/comments', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    if (!response.ok) {
      await refresh();
      throw new Error('Запись отклонена. Обновите страницу и повторите.');
    }
    await refresh();
  }

  const db = {
    collection(path) {
      return {
        add: data => write({ action: 'add', data }),
        onSnapshot(success, failure) {
          const listener = { success, failure };
          if (!watchers.has(path)) watchers.set(path, new Set());
          watchers.get(path).add(listener);
          success({ docs: records.filter(r => r.collection === path).map(r => ({ id: r.id, data: () => r.data })) });
          return () => watchers.get(path).delete(listener);
        },
      };
    },
    doc(path) {
      const record = records.find(r => r.collection + '/' + r.id === path);
      return {
        get: async () => ({ exists: !!record, data: () => record?.data }),
        update: data => write({ action: 'resolve', id: record.id, revision: record.revision, resolved: data.resolved }),
        delete: () => write({ action: 'delete', id: record.id, revision: record.revision }),
      };
    },
  };
  const ready = refresh();
  window.claude = { use: async name => {
    await ready;
    if (name === 'db') return db;
    return { me: async () => ({ id: owner, isOwner: true }), profiles: async () => ({}) };
  } };
  setInterval(refresh, 2000);
})();
