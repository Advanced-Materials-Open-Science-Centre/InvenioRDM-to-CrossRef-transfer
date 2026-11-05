import { api, db } from 'sdk';
import { eq } from 'sdk/db';
import { settings } from '../schema.js';
import { WORKFLOW_URL, GitHubError, checkToken, startWorkflow, latestRun } from '../lib/github.js';

const HELP = `Starts the New records workflow, which deposits new dataset.cnu.edu.ua records to Crossref.

/run - start the workflow now
/status - the latest run of the workflow
/whoami - your Telegram id and the chat's

Only the user who set the first token:
/settoken <token> - the GitHub token the bot starts the workflow with`;

const COMMANDS = [
  { command: 'run', description: 'Start the New records workflow' },
  { command: 'status', description: 'The latest run of the workflow' },
  { command: 'whoami', description: 'Your Telegram id and the chat\'s' },
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
      ? `${error.message}${error.status === 401 ? '\nThe token is wrong or has expired: whoever set it can /settoken a new one.' : ''}`
      : `Something went wrong: ${error.message}`);
    // Still reported in the logs
    console.error(error);
  }
}

async function answer(message, command, argument) {
  const from = message.from;
  if (!from) return 'Only a user can use this bot.';

  switch (command) {
    case 'start':
    case 'help':
    case '':
      return HELP;

    // The chat id is what the workflow's TELEGRAM_CHAT_IDS variable needs to send the results here
    case 'whoami':
      return `Your id is ${from.id}.${message.chat.id === from.id ? '' : ` This chat's id is ${message.chat.id}.`}`;

    case 'run':
      await startWorkflow(await token());
      return `Started the New records workflow. It takes a few seconds to show up in the runs: ${WORKFLOW_URL}`;

    case 'status': {
      const run = await latestRun(await token());
      if (!run) return 'The workflow hasn\'t run yet.';
      return `Latest run: ${run.conclusion ?? run.status}, started ${kyivTime(new Date(run.created_at))}.\n${run.html_url}`;
    }

    // Whoever sets the first token owns it, so set it right after deploying
    case 'settoken': {
      const owner = await setting('owner_id');
      if (owner && owner !== String(from.id)) return 'Only the user who set the token can change it.';
      // The token shouldn't stay in the chat history, wherever the check below ends up. In a group
      // that needs the bot to be an admin
      const deleted = await api.deleteMessage({ chat_id: message.chat.id, message_id: message.message_id })
        .then(() => true, () => false);
      if (!argument) return 'Send /settoken with a fine-grained GitHub token that has Actions: Read and write on the repository.';
      await checkToken(argument);
      await db.insert(settings).values({ key: 'github_token', value: argument })
        .onConflictDoUpdate({ target: settings.key, set: { value: argument } }).run();
      // Only a token that works makes its sender the owner
      if (!owner) {
        await db.insert(settings).values({ key: 'owner_id', value: String(from.id) }).run();
        await api.setMyCommands({ commands: COMMANDS });
      }
      return (deleted ? 'Saved the token and deleted your message with it.' : 'Saved the token. Delete your message with it: I couldn\'t.')
        + (owner ? '' : ' Only you can change it from now on.');
    }
  }

  return `I don't know /${command}. Send /help for what I can do.`;
}

async function setting(key) {
  const row = await db.select().from(settings).where(eq(settings.key, key)).get();
  return row?.value ?? null;
}

async function token() {
  const value = await setting('github_token');
  if (!value) throw new Error('the bot has no GitHub token yet: send /settoken with one.');
  return value;
}

function kyivTime(date) {
  return new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', dateStyle: 'medium', timeStyle: 'short' }).format(date);
}
