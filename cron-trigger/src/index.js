// Starts the New records workflow every day, because GitHub starts scheduled workflows hours late,
// and whenever someone asks the Telegram bot to

const HELP = `Starts the New records workflow, which deposits new dataset.cnu.edu.ua records to Crossref.

/run - start the workflow now
/status - the latest run of the workflow
/whoami - your Telegram id and the chat's`;

class GitHubError extends Error {
  constructor(status, body) {
    super(`GitHub answered ${status}: ${body.slice(0, 300)}`);
    this.status = status;
  }
}

async function github(env, method, path, body) {
  const response = await fetch(`https://api.github.com/repos/${env.REPOSITORY}/actions/workflows/${env.WORKFLOW}${path}`, {
    method,
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${env.GITHUB_TOKEN}`,
      'User-Agent': 'new-records-cron-trigger',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new GitHubError(response.status, await response.text());
  return response.status === 204 ? null : response.json();
}

const startWorkflow = env => github(env, 'POST', '/dispatches', { ref: env.BRANCH });

export default {
  async scheduled(controller, env) {
    // Cron Triggers run on UTC, so both UTC times that can be 19:17 in Kyiv are scheduled and the
    // one that isn't, depending on daylight saving time, is skipped
    const hour = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', hour: '2-digit', hourCycle: 'h23' })
      .format(controller.scheduledTime);
    if (hour !== '19') return;

    for (let attempt = 1; ; attempt++) {
      try {
        return await startWorkflow(env);
      } catch (error) {
        // Only a server error can go away by itself
        if (!(error instanceof GitHubError) || error.status < 500 || attempt === 3) throw error;
      }
      await new Promise(resolve => setTimeout(resolve, 10_000));
    }
  },

  // Telegram's webhook. The reply goes back in the response, so the Worker needs no bot token
  async fetch(request, env) {
    if (request.method !== 'POST' || new URL(request.url).pathname !== '/telegram') {
      return new Response('Not found', { status: 404 });
    }
    // Telegram sends the secret_token given to setWebhook, so nobody else can post updates
    if (!env.TELEGRAM_WEBHOOK_SECRET || request.headers.get('X-Telegram-Bot-Api-Secret-Token') !== env.TELEGRAM_WEBHOOK_SECRET) {
      return new Response('Forbidden', { status: 403 });
    }

    const { message } = await request.json();
    if (!message?.text || !message.from) return new Response(null);

    // With privacy mode on, which is the default, a group only sends the bot commands, and only
    // the ones meant for it
    const match = message.text.match(/^\/([a-z_]+)(?:@\w+)?(?:\s|$)/i);
    if (!match && message.chat.type !== 'private') return new Response(null);

    let text;
    // Telegram delivers an update again until it gets a 200, and a second /run would start the
    // workflow twice, so errors are answered instead of thrown
    try {
      text = await answer(env, message, match?.[1].toLowerCase());
    } catch (error) {
      console.error(error);
      text = error instanceof GitHubError && error.status === 401
        ? 'GitHub doesn\'t accept the token anymore: renew GITHUB_TOKEN in the Worker.'
        : `Something went wrong: ${error.message}`;
    }

    return Response.json({
      method: 'sendMessage',
      chat_id: message.chat.id,
      text,
      reply_parameters: { message_id: message.message_id, allow_sending_without_reply: true },
      link_preview_options: { is_disabled: true },
    });
  },
};

async function answer(env, message, command) {
  switch (command) {
    case 'run':
      await startWorkflow(env);
      return `Started the New records workflow. It takes a few seconds to show up in the runs: https://github.com/${env.REPOSITORY}/actions/workflows/${env.WORKFLOW}`;

    case 'status': {
      const { workflow_runs: [run] } = await github(env, 'GET', '/runs?per_page=1');
      if (!run) return 'The workflow hasn\'t run yet.';
      const started = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', dateStyle: 'medium', timeStyle: 'short' })
        .format(new Date(run.created_at));
      return `Latest run: ${run.conclusion ?? run.status}, started ${started}.\n${run.html_url}`;
    }

    // The chat id is what the workflow's TELEGRAM_CHAT_IDS variable needs to send the results here
    case 'whoami':
      return `Your id is ${message.from.id}.${message.chat.id === message.from.id ? '' : ` This chat's id is ${message.chat.id}.`}`;

    case 'start':
    case 'help':
    case undefined:
      return HELP;

    default:
      return `I don't know /${command}. Send /help for what I can do.`;
  }
}
