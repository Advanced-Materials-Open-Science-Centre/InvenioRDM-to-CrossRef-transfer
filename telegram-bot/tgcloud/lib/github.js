import { fetch } from 'sdk';

export const REPOSITORY = 'Advanced-Materials-Open-Science-Centre/InvenioRDM-to-CrossRef-transfer';
export const WORKFLOW = 'new-records.yml';
export const BRANCH = 'master';

export const WORKFLOW_URL = `https://github.com/${REPOSITORY}/actions/workflows/${WORKFLOW}`;

export class GitHubError extends Error {
  constructor(status, body) {
    super(`GitHub answered ${status}: ${body}`);
    this.status = status;
  }
}

async function request(token, method, path, body) {
  const response = await fetch(`https://api.github.com/repos/${REPOSITORY}/actions/workflows/${WORKFLOW}${path}`, {
    method,
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
      'User-Agent': 'new-records-telegram-bot',
      'X-GitHub-Api-Version': '2022-11-28',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new GitHubError(response.status, (await response.text()).slice(0, 300));
  return response.status === 204 ? null : response.json();
}

// Needs Actions: Read, so a token that passes can at least see the workflow
export function checkToken(token) {
  return request(token, 'GET', '');
}

export function startWorkflow(token) {
  return request(token, 'POST', '/dispatches', { ref: BRANCH });
}

export async function latestRun(token) {
  const { workflow_runs: runs } = await request(token, 'GET', '/runs?per_page=1');
  return runs[0] ?? null;
}
