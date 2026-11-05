import { table, integer, text, boolean } from 'sdk/db';

// Telegram Serverless has no secrets store, so the GitHub token is kept here under 'github_token'
export const settings = table('settings', {
  key: text('key').primaryKey(),
  value: text('value').notNull(),
});

// The Telegram users who may start the workflow. The owner also manages them and the token
export const users = table('users', {
  id: integer('id').primaryKey(),
  name: text('name'),
  owner: boolean('owner').notNull().default(false),
  addedAt: integer('added_at', { mode: 'timestamp' }).notNull(),
});

// Who started the workflow from Telegram and when, because GitHub shows the token's owner instead
export const dispatches = table('dispatches', {
  id: integer('id').primaryKey({ autoIncrement: true }),
  userId: integer('user_id').notNull(),
  name: text('name').notNull(),
  at: integer('at', { mode: 'timestamp' }).notNull(),
});
