#!/usr/bin/env node
// Generates every artefact that describes the API from the one definition,
// managed/Piper.Contracts/piper-api.json (target-architecture P3):
//
//   web/openapi.json                    the OpenAPI 3.1 description both servers serve
//   web/api.mjs                         the dashboard's client, one operation per route
//   managed/Piper.Core/PiperApi.g.cs    the route table the Host dispatches, guards and
//                                       admits by, and the typed C# request builders
//
//   node tools/gen-api.mjs           rewrites the three files
//   node tools/gen-api.mjs --check   writes nothing; exits 1 when a file differs (CI drift check)
//
// Output is a pure function of the definition: no timestamps, fixed key order, LF.
import { readFileSync, writeFileSync } from 'node:fs';

const root = new URL('../', import.meta.url);
const SOURCE = 'managed/Piper.Contracts/piper-api.json';
const api = JSON.parse(readFileSync(new URL(SOURCE, root), 'utf8'));

const fail = message => { throw Error(`${SOURCE}: ${message}`); };
const CLASSES = ['read', 'control', 'stop', 'maintenance'], BACKENDS = ['c', 'managed', 'both'];
const PIKA_PREFIX = '/api/pika/';

// ---- The definition, checked once -------------------------------------------------------------

/* A request field is a JSON Schema fragment, or {"$field": name} for one shared definition. */
const field = schema => {
  if (!('$field' in schema)) return schema;
  return api.fields[schema.$field] ?? fail(`unknown shared field ${schema.$field}`);
};
/* The named properties a request body may carry, and the ones it must: a route's own list, or
   those of the component schema it names (through allOf and $ref; conditional rules add none). */
function shape(schema) {
  if (schema.$ref) return shape(api.schemas[schema.$ref.replace('#/components/schemas/', '')]);
  const properties = {}, required = new Set(schema.required ?? []);
  for (const part of schema.allOf ?? []) {
    if (!part.$ref && !part.properties) continue;
    const inner = shape(part);
    Object.assign(properties, inner.properties); for (const name of inner.required) required.add(name);
  }
  for (const [name, value] of Object.entries(schema.properties ?? {})) properties[name] = field(value);
  return { properties, required: [...required] };
}
const requestSchema = request => request.schema
  ? (api.schemas[request.schema] ? { $ref: `#/components/schemas/${request.schema}` } : fail(`unknown schema ${request.schema}`))
  : { type: 'object', ...(request.required ? { required: request.required } : {}), properties: Object.fromEntries(Object.entries(request.properties).map(([name, value]) => [name, field(value)])) };

const ids = new Set(), endpoints = new Set();
const routes = api.routes.map(route => {
  const where = `route ${route.id ?? route.path}`;
  if (!/^[a-z][A-Za-z]*$/.test(route.id ?? '') || ids.has(route.id)) fail(`${where}: id must be a unique camelCase name`);
  if (!['GET', 'POST'].includes(route.method) || !/^\/api\/[a-z./-]+$/.test(route.path)) fail(`${where}: bad method or path`);
  if (endpoints.has(`${route.method} ${route.path}`)) fail(`${where}: duplicate method and path`);
  if (!CLASSES.includes(route.class) || !BACKENDS.includes(route.backend)) fail(`${where}: bad class or backend`);
  ids.add(route.id); endpoints.add(`${route.method} ${route.path}`);
  const post = route.method === 'POST';
  if (post !== (route.class !== 'read')) fail(`${where}: GET routes are class read, POST routes are not`);
  if (post && (!['session', 'open'].includes(route.owner) || typeof route.grpc !== 'boolean' || !route.request)) fail(`${where}: a POST route names owner, grpc and request`);
  if (!post && (route.owner || route.grpc || route.request || route.examples)) fail(`${where}: a GET route has no owner, grpc, request or examples`);
  // STOP is open to every authenticated caller and reachable on every transport its server has.
  if (route.class === 'stop' && (route.owner !== 'open' || !route.grpc)) fail(`${where}: a stop route is open and served over gRPC`);
  if (route.grpc && route.backend === 'c') fail(`${where}: only the managed host serves gRPC`);
  // The contract test sends every example over both transports, so each tunnelled route has one.
  if (route.grpc && !route.examples?.length) fail(`${where}: a gRPC route needs at least one example`);
  const body = post ? shape(requestSchema(route.request)) : null;
  for (const example of route.examples ?? []) for (const name of Object.keys(example.body))
    if (!(name in body.properties)) fail(`${where}: example ${example.name} carries the undefined field ${name}`);
  for (const name of body?.required ?? []) if (!(name in body.properties)) fail(`${where}: required field ${name} is undefined`);
  if (!Object.keys(route.responses ?? {}).length) fail(`${where}: no responses`);
  return { ...route, post, body, pikaAction: post && route.path.startsWith(PIKA_PREFIX) ? route.path.slice(PIKA_PREFIX.length) : null };
});
const backendOf = path => {
  const all = new Set(routes.filter(route => route.path === path).map(route => route.backend));
  return all.size === 1 ? [...all][0] : fail(`${path}: its methods name different backends`);
};

// ---- web/openapi.json -------------------------------------------------------------------------

function openapi() {
  const paths = {};
  for (const route of routes) {
    const item = paths[route.path] ??= { 'x-piper-backend': backendOf(route.path) };
    const operation = { operationId: route.id, summary: route.summary };
    if (route.description) operation.description = route.description;
    operation['x-piper-class'] = route.class;
    if (route.post) { operation['x-piper-owner'] = route.owner; operation['x-piper-grpc'] = route.grpc; }
    if (route.public) operation.security = [];
    if (route.query) operation.parameters = route.query.map(parameter => ({ name: parameter.name, in: 'query', schema: parameter.schema, description: parameter.description }));
    if (route.post) {
      const content = { schema: requestSchema(route.request) };
      if (route.examples) content.examples = Object.fromEntries(route.examples.map(example => [example.name, { value: example.body }]));
      operation.requestBody = { required: true, content: { 'application/json': content } };
    }
    operation.responses = Object.fromEntries(Object.entries(route.responses).map(([status, response]) => [status, {
      description: response.description,
      ...(response.schema ? { content: { 'application/json': { schema: { $ref: `#/components/schemas/${api.schemas[response.schema] ? response.schema : fail(`unknown schema ${response.schema}`)}` } } } } : {}),
      ...(response.content ? { content: response.content } : {}),
    }]));
    item[route.method.toLowerCase()] = operation;
  }
  return JSON.stringify({
    openapi: '3.1.0',
    info: { title: api.title, version: api.version, description: api.description },
    servers: [{ url: api.server }],
    security: [{ localToken: [] }],
    'x-piper-limits': api.limits,
    'x-piper-common-responses': api.common_responses,
    paths,
    components: { securitySchemes: { localToken: { type: 'apiKey', in: 'header', name: 'X-Piper-Token' } }, schemas: api.schemas },
  }, null, 2) + '\n';
}

// ---- web/api.mjs ------------------------------------------------------------------------------

const jsType = schema => {
  if ('const' in schema) return JSON.stringify(schema.const);
  if (schema.enum) return schema.enum.map(value => JSON.stringify(value)).join('|');
  if (schema.oneOf) return schema.oneOf.map(jsType).join('|');
  if (Array.isArray(schema.type)) return schema.type.map(type => jsType({ ...schema, type })).join('|');
  switch (schema.type) {
    case 'integer': case 'number': return 'number';
    case 'array': return `${jsType(schema.items ?? {})}[]`;
    case 'string': case 'boolean': case 'null': case 'object': return schema.type;
    default: return '*';
  }
};
function javascript() {
  const out = [
    `// Generated by tools/gen-api.mjs from ${SOURCE}. Do not edit: change the`,
    '// definition and regenerate (`node tools/gen-api.mjs`); CI fails when this file drifts.',
    '//',
    '// The dashboard\'s only way to reach the API: one operation per route. An operation sends',
    '// exactly the fields its caller passes, adds nothing, never retries and answers the fetch',
    '// Response. Every operation except the STOP class refuses a field its route does not',
    '// define, so a request the definition does not describe cannot be built. STOP operations',
    '// check nothing: no client-side state or mistake may keep a stop from being sent.',
    '',
    '/** Parses a reply; throws its error when the server refused the request. */',
    'export async function reply(response, fallback = \'Request failed.\') {',
    '  const result = await response.json();',
    '  if (!response.ok) throw Error(result.error || fallback);',
    '  return result;',
    '}',
    '',
    '/**',
    ' * @param {{ token?: () => string, fetcher: typeof fetch }} options `token` reads the current',
    ' *   X-Piper-Token for each POST. Each operation takes `(fields, { timeout, keepalive, token })`:',
    ' *   `timeout` in milliseconds aborts the request, `token` overrides the client\'s for one call.',
    ' */',
    'export function createApi({ token = () => \'\', fetcher }) {',
    '  const signal = timeout => (timeout ? { signal: AbortSignal.timeout(timeout) } : {});',
    '  async function post(path, defined, fields = {}, { timeout, keepalive = false, token: sent = token() } = {}) {',
    '    if (defined) for (const name of Object.keys(fields)) if (!defined.includes(name)) throw TypeError(`${path} defines no field ${name}`);',
    '    return fetcher(path, { method: \'POST\', headers: { \'Content-Type\': \'application/json\', \'X-Piper-Token\': sent }, body: JSON.stringify(fields), ...(keepalive ? { keepalive: true } : {}), ...signal(timeout) });',
    '  }',
    '  async function get(path, defined, fields = {}, { timeout } = {}) {',
    '    const query = new URLSearchParams();',
    '    for (const [name, value] of Object.entries(fields)) {',
    '      if (!defined.includes(name)) throw TypeError(`${path} defines no parameter ${name}`);',
    '      if (value !== undefined && value !== null) query.set(name, value);',
    '    }',
    '    return fetcher(query.size ? `${path}?${query}` : path, { cache: \'no-store\', ...signal(timeout) });',
    '  }',
    '  return Object.freeze({',
  ];
  for (const route of routes) {
    const properties = route.post ? route.body.properties : Object.fromEntries((route.query ?? []).map(parameter => [parameter.name, parameter.schema]));
    const required = new Set(route.body?.required ?? []);
    const type = Object.entries(properties).map(([name, schema]) => `${name}${required.has(name) ? '' : '?'}: ${jsType(schema)}`).join(', ');
    const names = `[${Object.keys(properties).map(name => `'${name}'`).join(', ')}]`;
    out.push(`    /** ${route.method} ${route.path} (${route.backend === 'both' ? 'both servers' : route.backend === 'c' ? 'C server only' : 'managed host only'}${route.class === 'stop' ? '; STOP class: never checked' : ''}). ${route.summary}.`);
    out.push(`     *  @param {{ ${type} }} [fields] */`);
    out.push(route.post
      ? `    ${route.id}: (fields, options) => post('${route.path}', ${route.class === 'stop' ? 'null' : names}, fields, options),`
      : `    ${route.id}: (fields, options) => get('${route.path}', ${names}, fields, options),`);
  }
  out.push('  });', '}', '');
  return out.join('\n');
}

// ---- managed/Piper.Core/PiperApi.g.cs -----------------------------------------------------------

const pascal = name => name.replace(/(^|[_-])([a-z])/g, (_, __, letter) => letter.toUpperCase());
const camel = name => name.replace(/_([a-z])/g, (_, letter) => letter.toUpperCase());
const csString = text => JSON.stringify(text);
/* [parameter type, expression that turns the parameter into a JsonNode]. */
function csType(schema) {
  if (Array.isArray(schema.type) && schema.type.length === 2 && schema.type.includes('null') && schema.type.includes('string')) return 'string?';
  if (schema.const === true || schema.type === 'boolean') return 'bool';
  if (typeof schema.const === 'string' || schema.type === 'string') return 'string';
  if (schema.type === 'integer') return 'long';
  if (schema.type === 'number') return 'double';
  return 'JsonNode'; // A union (e.g. one number or six): the caller builds the value.
}
function csharp() {
  const out = [
    '// <auto-generated>',
    `// Generated by tools/gen-api.mjs from ${SOURCE}. Do not edit: change the`,
    '// definition and regenerate (`node tools/gen-api.mjs`); CI fails when this file drifts.',
    '// </auto-generated>',
    '#nullable enable',
    'using System.Text.Json.Nodes;',
    '',
    'namespace LuckyRobots.Piper;',
    '',
    'public static partial class PiperApi',
    '{',
    '    /// <summary>Largest control request body, in UTF-8 bytes.</summary>',
    `    public const int MaxBodyBytes = ${api.limits.max_body_bytes};`,
    '    /// <summary>Deepest JSON nesting a control request may use.</summary>',
    `    public const int MaxDepth = ${api.limits.max_depth};`,
    '',
    '    /// <summary>Route paths as constants, for patterns and attributes.</summary>',
    '    public static class Paths',
    '    {',
    ...routes.map(route => `        public const string ${pascal(route.id)} = ${csString(route.path)};`),
    '    }',
    '',
    ...routes.map(route => `    public static readonly ApiRoute ${pascal(route.id)} = new(${csString(route.id)}, ${csString(route.method)}, Paths.${pascal(route.id)}, ApiRouteClass.${pascal(route.class)}, ApiBackend.${pascal(route.backend)}, SessionOwned: ${route.owner === 'session'}, Grpc: ${route.grpc === true}, PikaAction: ${route.pikaAction === null ? 'null' : csString(route.pikaAction)});`),
    '',
    '    /// <summary>Every route of the API, in the definition\'s order.</summary>',
    `    public static readonly IReadOnlyList<ApiRoute> Routes = [${routes.map(route => pascal(route.id)).join(', ')}];`,
    '',
    '    /// <summary>One builder per POST route: named, typed fields in; the route and its JSON body out.',
    '    /// An omitted optional field is not sent.</summary>',
    '    public static class Requests',
    '    {',
  ];
  for (const route of routes.filter(route => route.post)) {
    const required = new Set(route.body.required);
    const fields = Object.entries(route.body.properties).map(([name, schema]) => ({ name, parameter: camel(name), type: csType(schema), required: required.has(name) }));
    const ordered = [...fields.filter(f => f.required), ...fields.filter(f => !f.required)];
    const parameters = ordered.map(f => {
      if (f.required) return `${f.type === 'string?' ? 'string?' : f.type} ${f.parameter}`;
      if (f.type === 'string?') return `ApiOptional<string?> ${f.parameter} = default`;
      return `${f.type}? ${f.parameter} = null`;
    });
    out.push(`        /// <summary>${route.method} ${route.path}: ${route.summary.replace(/&/g, '&amp;').replace(/</g, '&lt;')}.</summary>`);
    out.push(`        public static ApiRequest ${pascal(route.id)}(${parameters.join(', ')})`);
    out.push('        {', '            var body = new JsonObject();');
    for (const f of ordered) {
      const value = f.type === 'JsonNode' ? `${f.parameter}${f.required ? '' : '!'}.DeepClone()` : f.type === 'string?' ? (f.required ? f.parameter : `${f.parameter}.Value`) : f.required || f.type === 'string' ? f.parameter : `${f.parameter}.Value`;
      const guard = f.required ? '' : f.type === 'string?' ? `if (${f.parameter}.Present) ` : `if (${f.parameter} is not null) `;
      out.push(`            ${guard}body[${csString(f.name)}] = ${value};`);
    }
    out.push(`            return new ApiRequest(PiperApi.${pascal(route.id)}, body);`, '        }');
  }
  out.push('    }', '}', '');
  return out.join('\n');
}

// ---- Write or check ---------------------------------------------------------------------------

const outputs = { 'web/openapi.json': openapi(), 'web/api.mjs': javascript(), 'managed/Piper.Core/PiperApi.g.cs': csharp() };
const check = process.argv.includes('--check');
const drifted = [];
for (const [file, text] of Object.entries(outputs)) {
  let current = null;
  try { current = readFileSync(new URL(file, root), 'utf8'); } catch { /* absent */ }
  if (current === text) continue;
  drifted.push(file);
  if (!check) writeFileSync(new URL(file, root), text);
}
if (check && drifted.length) {
  console.error(`Generated API files differ from ${SOURCE}: ${drifted.join(', ')}.\nRun \`node tools/gen-api.mjs\` and commit the result.`);
  process.exit(1);
}
console.log(check ? `API artefacts match ${SOURCE} (${routes.length} routes).` : `Generated ${Object.keys(outputs).length} API artefacts from ${SOURCE} (${routes.length} routes${drifted.length ? `; rewrote ${drifted.join(', ')}` : '; nothing changed'}).`);
