// Starts the New records workflow, because GitHub starts scheduled workflows hours late
export default {
  async scheduled(controller, env) {
    // Cron Triggers run on UTC, so both UTC times that can be 19:17 in Kyiv are scheduled and the
    // one that isn't, depending on daylight saving time, is skipped
    const hour = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', hour: '2-digit', hourCycle: 'h23' })
      .format(controller.scheduledTime);
    if (hour !== '19') return;

    const url = `https://api.github.com/repos/${env.REPOSITORY}/actions/workflows/${env.WORKFLOW}/dispatches`;
    for (let attempt = 1; ; attempt++) {
      const response = await fetch(url, {
        method: 'POST',
        headers: {
          Accept: 'application/vnd.github+json',
          Authorization: `Bearer ${env.GITHUB_TOKEN}`,
          'User-Agent': 'new-records-cron-trigger',
          'X-GitHub-Api-Version': '2022-11-28',
        },
        body: JSON.stringify({ ref: env.BRANCH }),
      });
      if (response.ok) return;

      // Only a server error can go away by itself
      if (response.status < 500 || attempt === 3) {
        throw new Error(`GitHub answered ${response.status}: ${await response.text()}`);
      }
      await new Promise(resolve => setTimeout(resolve, 10_000));
    }
  },
};
