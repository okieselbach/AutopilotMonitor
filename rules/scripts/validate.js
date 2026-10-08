#!/usr/bin/env node
/**
 * Validates every rule file and rules/guardrails.json against its JSON Schema
 * (draft 2020-12). combine-rules.yml runs this before combine.js; any invalid
 * file fails the run with exit code 1.
 *
 * Run: node rules/scripts/validate.js   (after `npm ci --ignore-scripts` in rules/scripts)
 */

const fs = require('fs');
const path = require('path');
const Ajv2020 = require('ajv/dist/2020');
const addFormats = require('ajv-formats');

const rulesRoot = path.resolve(__dirname, '..');

const jsonFilesIn = (dir) =>
  fs.readdirSync(path.join(rulesRoot, dir)).filter(f => f.endsWith('.json')).sort().map(f => `${dir}/${f}`);

const sets = [
  { schema: 'gather-rule.schema.json', files: jsonFilesIn('gather') },
  { schema: 'analyze-rule.schema.json', files: jsonFilesIn('analyze') },
  { schema: 'ime-log-pattern.schema.json', files: jsonFilesIn('ime-log-patterns') },
  // guardrails.json values are baked verbatim into generated TypeScript that is
  // auto-committed to main and shipped to the portal - the schema forbids control
  // and line-terminator characters so a "data" PR can never become code.
  { schema: 'guardrails.schema.json', files: ['guardrails.json'] }
];

const ajv = new Ajv2020({ allErrors: true });
addFormats(ajv);

let invalid = 0;

for (const set of sets) {
  const validate = ajv.compile(JSON.parse(fs.readFileSync(path.join(rulesRoot, 'schema', set.schema), 'utf8')));

  for (const file of set.files) {
    let data;
    try {
      data = JSON.parse(fs.readFileSync(path.join(rulesRoot, file), 'utf8'));
    } catch (err) {
      invalid++;
      console.error(`INVALID rules/${file}: ${err.message}`);
      continue;
    }

    if (!validate(data)) {
      invalid++;
      console.error(`INVALID rules/${file}`);
      for (const e of validate.errors) {
        console.error(`  ${e.instancePath || '(root)'} ${e.message} ${JSON.stringify(e.params)}`);
      }
    }
  }

  console.log(`${set.schema}: ${set.files.length} files checked`);
}

if (invalid > 0) {
  console.error(`ERROR: ${invalid} file(s) failed schema validation`);
  process.exit(1);
}
