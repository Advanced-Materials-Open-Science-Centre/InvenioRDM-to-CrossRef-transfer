import { table, text } from 'sdk/db';

// 'github_token', kept here because Telegram Serverless has no secrets store, and 'owner_id', the
// user who set the first token and the only one who may change it
export const settings = table('settings', {
  key: text('key').primaryKey(),
  value: text('value').notNull(),
});
