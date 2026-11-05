import { table, integer, text } from 'sdk/db';

// 'github_token', kept here because Telegram Serverless has no secrets store, and 'owner_id', the
// only user who may change it
export const settings = table('settings', {
  key: text('key').primaryKey(),
  value: text('value').notNull(),
});

// Who started the workflow from Telegram and when, because GitHub shows the token's owner instead
export const dispatches = table('dispatches', {
  id: integer('id').primaryKey({ autoIncrement: true }),
  userId: integer('user_id').notNull(),
  name: text('name').notNull(),
  at: integer('at', { mode: 'timestamp' }).notNull(),
});
