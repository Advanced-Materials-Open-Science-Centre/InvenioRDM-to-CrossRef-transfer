import { api, db } from 'sdk';
import { eq, desc } from 'sdk/db';
import { settings, users, dispatches } from '../schema.js';
import { WORKFLOW_URL, GitHubError, checkToken, startWorkflow, latestRun } from '../lib/github.js';

const HELP = `Starts the New records workflow, which deposits new dataset.cnu.edu.ua records to Crossref.

/run - start the workflow now
/status - the latest run of the workflow
/whoami - your Telegram id, for the owner to /allow, and the chat's

Owner only:
/allow <id> - let a user start the workflow
/deny <id> - take that back
/users - who may start it
/settoken <token> - the GitHub token the bot starts the workflow with`;

const COMMANDS = [
  { command: 'run', description: 'Start the New records workflow' },
  { command: 'status', description: 'The latest run of the workflow' },
  { command: 'whoami', description: 'Your Telegram id' },
  { command: 'help', description: 'What the bot can do' },
];

export default async function (message) {
  const text = message.text ?? '';
  const match = text.match(/^\/([a-z_]+)(?:@(\w+))?(?:\s+([\s\S]*))?$/i);
  // Outside a private chat the bot only answers its commands, so it can sit in a group
  if (!match && message.chat.type !== 'private') return;

  const [, command = '', mention, argument = ''] = match ?? [];
  if (mention && mention.toLowerCase() !== (await api.getMe()).username.toLowerCase()) return;

  const reply = text => api.sendMessage({
    chat_id: message.chat.id,
    text,
    reply_parameters: { message_id: message.message_id, allow_sending_without_reply: true },
    link_preview_options: { is_disabled: true },
  });

  // Anything thrown would fail the update, and a redelivered /run would start the workflow twice
  try {
    await reply(await answer(message, command.toLowerCase(), argument.trim()));
  } catch (error) {
    await reply(error instanceof GitHubError
      ? `${error.message}${error.status === 401 ? '\nThe token is wrong or has expired: the owner can /settoken a new one.' : ''}`
      : `Something went wrong: ${error.message}`);
    // Still reported in the logs
    console.error(error);
  }
}

async function answer(message, command, argument) {
  const from = message.from;
  if (!from) return 'Only a user can use this bot.';

  const user = await db.select().from(users).where(eq(users.id, from.id)).get();
  const name = [from.first_name, from.last_name].filter(Boolean).join(' ') + (from.username ? ` (@${from.username})` : '');
  if (user && user.name !== name) await db.update(users).set({ name }).where(eq(users.id, from.id)).run();

  switch (command) {
    case 'start':
    case 'help':
    case '':
      return `${HELP}\n\nYour id is ${from.id}.${user ? '' : ' You can\'t start the workflow until the owner allows you.'}`;

    // The chat id is what the workflow's TELEGRAM_CHAT_IDS variable needs to send the results here
    case 'whoami':
      return `Your id is ${from.id}.${message.chat.id === from.id ? '' : ` This chat's id is ${message.chat.id}.`}`;

    // Whoever claims the bot first becomes its owner, so claim it right after deploying
    case 'claim': {
      if (await db.$count(users, eq(users.owner, true))) return 'The bot already has an owner.';
      await db.insert(users).values({ id: from.id, name, owner: true, addedAt: new Date() })
        .onConflictDoUpdate({ target: users.id, set: { owner: true } }).run();
      await api.setMyCommands({ commands: COMMANDS });
      return 'You own the bot now. Send /settoken with a GitHub token next.';
    }
  }

  if (!user) return `You can't use this bot yet. Ask its owner to /allow ${from.id}.`;

  switch (command) {
    case 'run': {
      await startWorkflow(await token());
      await db.insert(dispatches).values({ userId: from.id, name, at: new Date() }).run();
      return `Started the New records workflow. It takes a few seconds to show up in the runs: ${WORKFLOW_URL}`;
    }

    case 'status': {
      const run = await latestRun(await token());
      const last = await db.select().from(dispatches).orderBy(desc(dispatches.id)).limit(1).get();
      const started = last ? `\nLast started from Telegram by ${last.name} at ${kyivTime(last.at)}.` : '';
      if (!run) return `The workflow hasn't run yet.${started}`;
      return `Latest run: ${run.conclusion ?? run.status}, started ${kyivTime(new Date(run.created_at))}.\n${run.html_url}${started}`;
    }
  }

  if (!user.owner) return 'Only the owner can do that.';

  switch (command) {
    case 'allow': {
      const id = Number(argument);
      if (!Number.isSafeInteger(id) || id <= 0) return 'Send /allow with the user\'s id. They get it from /whoami.';
      if (await db.$count(users, eq(users.id, id))) return `User ${id} can already start the workflow.`;
      await db.insert(users).values({ id, addedAt: new Date() }).run();
      return `User ${id} can start the workflow now.`;
    }

    case 'deny': {
      const id = Number(argument);
      if (!Number.isSafeInteger(id)) return 'Send /deny with the user\'s id from /users.';
      if (id === from.id) return 'You can\'t remove yourself.';
      const { rowsAffected } = await db.run('DELETE FROM users WHERE id = :id AND owner = 0', { ':id': id });
      return rowsAffected ? `User ${id} can't start the workflow anymore.` : `${argument || 'That'} isn't an allowed user.`;
    }

    case 'users': {
      const rows = await db.select().from(users).orderBy(users.addedAt).all();
      return rows.map(row => `${row.id} ${row.name ?? '(hasn\'t used the bot yet)'}${row.owner ? ' - owner' : ''}`).join('\n');
    }

    case 'settoken': {
      // The token shouldn't stay in the chat history, wherever the check below ends up
      // In a group that needs the bot to be an admin
      const deleted = await api.deleteMessage({ chat_id: message.chat.id, message_id: message.message_id })
        .then(() => true, () => false);
      if (!argument) return 'Send /settoken with a fine-grained GitHub token that has Actions: Read and write on the repository.';
      await checkToken(argument);
      await db.insert(settings).values({ key: 'github_token', value: argument })
        .onConflictDoUpdate({ target: settings.key, set: { value: argument } }).run();
      return deleted ? 'Saved the token and deleted your message with it.' : 'Saved the token. Delete your message with it: I couldn\'t.';
    }
  }

  return `I don't know /${command}. Send /help for what I can do.`;
}

async function token() {
  const row = await db.select().from(settings).where(eq(settings.key, 'github_token')).get();
  if (!row) throw new Error('the bot has no GitHub token yet: the owner has to /settoken one.');
  return row.value;
}

function kyivTime(date) {
  return new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', dateStyle: 'medium', timeStyle: 'short' }).format(date);
}
