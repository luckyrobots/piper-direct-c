import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
const source=await readFile(new URL('../web/app.js',import.meta.url),'utf8');
const nodes=new Map();let shown=0,closed=0;
const element=id=>{if(!nodes.has(id))nodes.set(id,{showModal(){shown++;},close(){closed++;}});return nodes.get(id);};
const context=vm.createContext({window:{},$:element});
vm.runInContext(source.slice(source.indexOf('/* Web-B operator approval'),source.indexOf('function showError')),context);
element('safety-continue').disabled=true;element('safety-cancel').disabled=true;
let first=context.window.confirmPiperControl(),second=context.window.confirmPiperControl();
assert.equal(shown,1,'Concurrent controls share one prompt');
assert.equal(element('safety-continue').disabled,false,'Reconnect must not leave the prompt disabled');
assert.equal(element('safety-cancel').disabled,false);
element('safety-cancel').onclick();assert.equal(await first,false);assert.equal(await second,false);
first=context.window.confirmPiperControl();element('safety-continue').onclick();assert.equal(await first,true);
assert.equal(await context.window.confirmPiperControl(),true);assert.equal(shown,2,'An accepted session needs no further prompt');
assert.equal(closed,2);
context.revokeApproval();first=context.window.confirmPiperControl();assert.equal(shown,3,'A revoked approval prompts again');
element('safety-continue').onclick();assert.equal(await first,true);
const html=await readFile(new URL('../web/index.html',import.meta.url),'utf8');
assert.doesNotMatch(html,/type="checkbox"/,'Dashboard must not retain confirmation checkboxes');
assert.doesNotMatch(html,/id="recover"/,'The always-hidden recover button is gone');

// Run the whole dashboard against a permissive fake DOM and record what it posts.
function fake(initial={}){
  const store={...initial};
  return new Proxy(function(){},{
    get(_,key){
      if(key in store)return store[key];
      if(key===Symbol.toPrimitive)return()=>'';
      if(typeof key==='symbol'||key==='then')return undefined;
      return store[key]=fake();
    },
    set(_,key,value){store[key]=value;return true;},
    apply(){return fake();},
  });
}
const dom=new Map(),listeners=new Map(),posts=[],dispatched=[];
const values={speed:'17',tolerance:'0.2',step:'1','gripper-effort':'0.1',width:'25','gripper-step':'1'};
const byId=id=>{if(!dom.has(id))dom.set(id,fake({value:values[id]??'',addEventListener:(name,fn)=>listeners.set(`${id}:${name}`,fn),showModal(){},close(){}}));return dom.get(id);};
const joint=index=>({index,fresh:true,enabled:false,actual_deg:0,min_deg:-150,max_deg:150,limits_fresh:true,max_speed_rad_s:1,velocity_rad_s:0});
const robot=(overrides={})=>({id:'simulation:one',name:'Sim',interface:'sim',present:true,fresh:true,generation:3,blocker:'',range_entry_deg:0,firmware_supported:true,recovery_required:false,reset_pending:false,status:{control:0,teach_state:0,arm_status:0,error:0},gripper:{},joints:[1,2,3,4,5,6].map(joint),...overrides});
let served={token:'t',simulated:true,selected:'simulation:one',phase:'monitor',message:'',query_active:false,motion:{active:false,request_id:0},devices:[robot()]};
let confirmed=true,answer=()=>({ok:true,json:async()=>({message:'ok'})});
const app=vm.createContext({
  window:{addEventListener(){},dispatchEvent:event=>dispatched.push(event),confirm:()=>confirmed},
  document:{getElementById:byId,createElement:()=>fake(),addEventListener(){},querySelector:()=>fake(),querySelectorAll:()=>[],hidden:false,hasFocus:()=>true,activeElement:null},
  crypto,performance,AbortSignal,JSON,Number,Math,Object,Set,Error,Promise,Date,
  CustomEvent:class{constructor(type,init){this.type=type;this.detail=init?.detail;}},
  Event:class{constructor(type){this.type=type;}},
  Option:class{},
  setTimeout:()=>0,setInterval:()=>0,clearTimeout:()=>{},
  fetch:async(route,init)=>{
    if(route==='/api/state')return{ok:true,json:async()=>JSON.parse(JSON.stringify(served))};
    posts.push({route,body:JSON.parse(init.body)});return answer(route);
  },
});
vm.runInContext(source,app);
const settle=async()=>{for(let i=0;i<20;i++)await new Promise(resolve=>setImmediate(resolve));};
await settle();
assert.equal(dispatched.at(-1)?.type,'piper-state','Each state poll is shared with pika.mjs');
assert.equal(dispatched.at(-1).detail.token,'t');
app.window.confirmPiperControl=async()=>true;
const flags=['standalone','clear','supported','reset_completed'];
const sent=()=>Object.fromEntries(flags.filter(key=>key in posts.at(-1).body).map(key=>[key,posts.at(-1).body[key]]));
const click=async id=>{await byId(id).onclick();await settle();};
await click('disable');
assert.equal(posts.at(-1).body.action,'disable');
assert.deepEqual(sent(),{standalone:true,supported:true},'disable reads only standalone and supported');
await click('reset');
assert.deepEqual(sent(),{standalone:true,clear:true,supported:true});
await click('cycle');
assert.equal(posts.at(-1).body.action,'cycle');
assert.deepEqual(sent(),{standalone:true,clear:true},'A normal command never carries reset_completed');
await click('hold');
assert.deepEqual(sent(),{standalone:true,clear:true});
await click('query');
assert.equal(posts.at(-1).route,'/api/query');
assert.deepEqual(sent(),{},'Non-command routes carry no safety confirmations');
confirmed=false;let before=posts.length;
await click('verify');
assert.equal(posts.length,before,'Declining the reset-completed prompt sends nothing');
confirmed=true;await click('verify');
assert.equal(posts.at(-1).body.action,'verify-reset');
assert.deepEqual(sent(),{standalone:true,supported:true,reset_completed:true},'Only the confirmed verify-reset carries reset_completed');

// B19: STOP while a command is in flight must not let that reply re-create the lease.
let finish;answer=route=>route==='/api/command'?new Promise(resolve=>{finish=()=>resolve({ok:true,json:async()=>({message:'moving',request_id:9})});}):{ok:true,json:async()=>({message:'stopped'})};
const moving=byId('cycle').onclick();await settle();
assert.equal(vm.runInContext('lease.phase',app),'pending');
before=posts.length;await byId('cycle').onclick();await settle();
assert.equal(posts.length,before,'Only one command is in flight');
await byId('stop').onclick();await settle();
assert.equal(posts.at(-1).route,'/api/stop');
assert.equal(vm.runInContext('lease.phase',app),'none');
finish();await moving;await settle();
assert.equal(vm.runInContext('lease.phase',app),'none','A reply that arrives after STOP must not re-create the lease');
answer=()=>({ok:true,json:async()=>({message:'moving',request_id:10})});
await click('cycle');
assert.deepEqual(JSON.parse(vm.runInContext('JSON.stringify(lease)',app)),{phase:'leased',request_id:10});
console.log('Compact controls passed: one shared prompt, cancellation sends no approval, per-action safety confirmations, confirmed reset_completed only, STOP retires in-flight replies, state shared with Pika.');
