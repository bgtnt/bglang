import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

// K-only draft: set BGLANG_CLIENT_SOURCE_ROOT to the repository root.
// Once copied to tests/BgLang.Web.Tests/Client, the relative fallback is portable.
const source = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, 'apps/BgLang.Web/wwwroot/live-client.js'))
  : new URL('../../../apps/BgLang.Web/wwwroot/live-client.js', import.meta.url);
const {createPostLane, livePayload, createLiveController, completeReadings,
  caretWord, completionSpan, applyCompletion, renderTokenText, continuationSnapshot, bindContinuation, applyContinuation} = await import(source.href);
const drain = () => new Promise(resolve => setImmediate(resolve));
const RAW = '😀 по-ле\u0300сен.\r\n<img src=x onerror=alert(1)>&';

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes,no) => { resolve=yes; reject=no; });
  return {promise,resolve,reject};
}

function clockFixture() {
  let time=0, next=0;
  const timers=new Map(), wallBase=Date.UTC(2026,9,24,0,0,0);
  return {
    now:()=>time, wallNow:()=>wallBase+time,
    setTimer(fn,delay=0) { const id=++next; timers.set(id,{at:time+Math.max(0,delay),fn}); return id; },
    clearTimer(id) { timers.delete(id); },
    async advance(ms) {
      const end=time+ms;
      for (;;) {
        const due=[...timers.entries()].filter(([,t])=>t.at<=end).sort((a,b)=>a[1].at-b[1].at||a[0]-b[0])[0];
        if (!due) break;
        timers.delete(due[0]); time=due[1].at; due[1].fn(); await drain();
      }
      time=end; await drain();
    }
  };
}

function transportFixture(clock) {
  const calls=[];
  let inFlight=0,maxInFlight=0;
  function fetchImpl(url,init) {
    const headers=deferred(),body=deferred();
    const call={url,init,start:clock.now(),headers,body,finished:false};
    calls.push(call); inFlight++; maxInFlight=Math.max(maxInFlight,inFlight);
    const finish=()=>{if(!call.finished){call.finished=true;inFlight--;}};
    return headers.promise.then(meta=>({
      status:meta.status,ok:meta.status>=200&&meta.status<300,
      headers:{get:name=>name.toLowerCase()==='retry-after'?(meta.retryAfter??null):null},
      json:()=>body.promise.finally(finish)
    }),error=>{finish();throw error;});
  }
  return {
    calls,fetchImpl,get maxInFlight(){return maxInFlight;},get inFlight(){return inFlight;},
    async headers(index,status=200,retryAfter=null) {calls[index].headers.resolve({status,retryAfter});await drain();},
    async finish(index,response={data:{text:'result'}},status=200,retryAfter=null) {
      await this.headers(index,status,retryAfter); calls[index].body.resolve(response); await drain();
    },
    async body(index,response) {calls[index].body.resolve(response);await drain();},
    async badBody(index,error=new SyntaxError('Malformed response JSON')) {calls[index].body.reject(error);await drain();},
    async fail(index,error=new Error('Network failure')) {calls[index].headers.reject(error);await drain();}
  };
}

function fixture(requestsPerMinute=30) {
  const clock=clockFixture(),net=transportFixture(clock);
  const lane=createPostLane({fetchImpl:net.fetchImpl,now:clock.now,wallNow:clock.wallNow,
    setTimer:clock.setTimer,clearTimer:clock.clearTimer,requestsPerMinute});
  return {clock,net,lane};
}

function liveFixture() {
  const base=fixture(),commits=[],invalidations=[],states=[];
  const controller=createLiveController({lane:base.lane,
    commit:(result,revision)=>commits.push({result,revision}),
    invalidate:(text,revision)=>invalidations.push({text,revision}),
    state:(name,extra)=>states.push({name,extra,time:base.clock.now()}),
    setTimer:base.clock.setTimer,clearTimer:base.clock.clearTimer,debounceMs:400,maxWaitMs:10000});
  return {...base,controller,commits,invalidations,states};
}

function posted(call) {assert.equal(call.init.method,'POST');return JSON.parse(call.init.body);}

test('payload preserves raw Unicode and explicitly carries every independent opt-in',()=>{
  const context={includeAdditionalRoleHeuristics:true,includeSubordinateCommaDiagnostics:true,
    includeFragmentDiagnostics:true,includeSpacingDiagnostics:true,includeWordConstructions:true};
  assert.deepEqual(livePayload(RAW),{text:RAW,evidenceFormat:'shared-analyses-v1',
    includeUnknownContextProperNames:true,context});
  assert.deepEqual(livePayload('  '+RAW+'\t',false),{text:'  '+RAW+'\t',evidenceFormat:'shared-analyses-v1',
    includeUnknownContextProperNames:false,context:Object.fromEntries(Object.keys(context).map(k=>[k,false]))});
});

test('typing burst debounces to exactly the latest raw payload and invalidates synchronously',async()=>{
  const f=liveFixture();
  f.controller.edit(' А '); await f.clock.advance(100);
  f.controller.edit(' Б '); await f.clock.advance(150);
  f.controller.edit(RAW);
  assert.equal(f.invalidations.at(-1).text,RAW);
  await f.clock.advance(399); assert.equal(f.net.calls.length,0);
  await f.clock.advance(1); assert.equal(f.net.calls.length,1);
  assert.equal(f.net.calls[0].start,650);
  assert.equal(f.net.calls[0].url,'/api/v2/analyze-text');
  assert.equal(posted(f.net.calls[0]).text,RAW);
  await f.net.finish(0,{data:{text:RAW}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].revision,f.controller.revision);
  await f.clock.advance(60000);assert.equal(f.net.calls.length,1,'No success polling loop.');
});

test('all operation routes share one lane and headers alone do not release the active body',async()=>{
  const f=fixture();
  const a=f.lane.post('analyze-text',{text:'A'},{key:'live'});
  const b=f.lane.post('generate-forms',{text:'книга',limit:32},{key:'word'});
  const c=f.lane.post('find-forms-in-text',{text:'книга',query:'книга'},{key:'comparison'});
  assert.equal(f.net.calls.length,1);
  await f.clock.advance(50);await f.net.headers(0);
  await f.clock.advance(2550);
  assert.equal(f.net.calls.length,1,'A parsed body, not headers or elapsed cadence, releases the lane.');
  await f.net.body(0,{data:{text:'A'}});await a;
  assert.equal(f.net.calls.length,2);assert.equal(f.net.calls[1].start,2600);
  assert.equal(f.net.calls[1].url,'/api/v2/generate-forms');
  await f.net.finish(1);await b;
  await f.clock.advance(2024);assert.equal(f.net.calls.length,2);
  await f.clock.advance(1);assert.equal(f.net.calls.length,3);
  assert.equal(f.net.calls[2].url,'/api/v2/find-forms-in-text');
  assert.equal(f.net.calls[2].start,4625);
  await f.net.finish(2);await c;
  assert.equal(f.net.maxInFlight,1);
});

test('host RPM configuration replaces a pending timer and invalid values restore the safe default',async()=>{
  const f=fixture();const a=f.lane.post('analyze-text',{text:'A'});await f.net.finish(0);await a;
  const b=f.lane.post('analyze-word',{text:'B'});
  await f.clock.advance(500);f.lane.configureBudget(60);
  await f.clock.advance(524);assert.equal(f.net.calls.length,1);
  await f.clock.advance(1);assert.equal(f.net.calls[1].start,1025);
  await f.net.finish(1);await b;
  const c=f.lane.post('decompose-word',{text:'C'});
  f.lane.configureBudget(0);
  await f.clock.advance(2024);assert.equal(f.net.calls.length,2);
  await f.clock.advance(1);assert.equal(f.net.calls[2].start,3050);
  await f.net.finish(2);await c;
});

test('pending jobs with the same key coalesce without aborting the active operation',async()=>{
  const f=fixture();
  const a=f.lane.post('analyze-text',{text:'A'},{key:'live'});
  const b=f.lane.post('analyze-text',{text:'B'},{key:'live'});
  const c=f.lane.post('analyze-text',{text:'C'},{key:'live'});
  assert.equal((await b).stale,true);
  assert.equal(f.net.calls.length,1);assert.equal(f.net.inFlight,1);
  await f.net.finish(0);await a;await f.clock.advance(2025);
  assert.equal(f.net.calls.length,2);assert.equal(posted(f.net.calls[1]).text,'C');
  await f.net.finish(1);await c;
});

test('old success cannot commit or report ready after newer edits, and only the latest revision runs',async()=>{
  const f=liveFixture();f.controller.edit('A');f.controller.flush();
  await f.clock.advance(100);f.controller.edit('B');f.controller.edit('C');
  assert.equal(f.invalidations.at(-1).text,'C');
  await f.net.finish(0,{data:{text:'A'}});
  assert.equal(f.commits.length,0);assert.equal(f.states.filter(s=>s.name==='ready').length,0);
  await f.clock.advance(1925);
  assert.equal(f.net.calls.length,2);assert.equal(posted(f.net.calls[1]).text,'C');
  await f.net.finish(1,{data:{text:'C'}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].result.response.data.text,'C');
  assert.equal(f.commits[0].revision,f.controller.revision);
});

test('same text with changed opt-ins invalidates the old request',async()=>{
  const f=liveFixture();f.controller.edit(RAW,true);f.controller.flush();
  await f.clock.advance(100);f.controller.edit(RAW,false);f.controller.flush();
  await f.net.finish(0,{data:{text:RAW,previousOptions:true}});
  assert.equal(f.commits.length,0);
  await f.clock.advance(1925);
  assert.deepEqual(posted(f.net.calls[1]),livePayload(RAW,false));
  await f.net.finish(1,{data:{text:RAW,previousOptions:false}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].result.response.data.previousOptions,false);
});

test('IME suppresses intermediate requests and commits the final composition exactly once',async()=>{
  const f=liveFixture();f.controller.edit('пред');f.controller.compositionStart();
  f.controller.edit('предв');await f.clock.advance(5000);
  assert.equal(f.net.calls.length,0);assert.equal(f.states.at(-1).name,'composing');
  f.controller.compositionEnd(RAW);f.controller.edit(RAW); // The browser may emit input after compositionend.
  await f.clock.advance(399);assert.equal(f.net.calls.length,0);
  await f.clock.advance(1);assert.equal(f.net.calls.length,1);assert.equal(posted(f.net.calls[0]).text,RAW);
  await f.net.finish(0,{data:{text:RAW}});assert.equal(f.commits.length,1);
});

test('pause, empty input and UTF-16 oversize clear pending work and make active results stale',async()=>{
  const f=liveFixture();
  f.controller.edit('😀'.repeat(2048)+'a');await f.clock.advance(10000);
  assert.equal(f.net.calls.length,0);assert.equal(f.states.at(-1).name,'oversize');
  f.controller.edit('😀'.repeat(2048));f.controller.flush();assert.equal(f.net.calls.length,1);
  f.controller.edit(' \r\n\t');assert.equal(f.states.at(-1).name,'empty');
  await f.net.finish(0);assert.equal(f.commits.length,0);
  f.controller.edit('книга');f.controller.setEnabled(false);await f.clock.advance(10000);
  assert.equal(f.net.calls.length,1);assert.equal(f.states.at(-1).name,'paused');
  f.controller.setEnabled(true);await f.clock.advance(400);assert.equal(f.net.calls.length,2);
  await f.net.finish(1);assert.equal(f.commits.length,1);
});

test('stale 429 still imposes Retry-After on a newer action in another route',async()=>{
  const f=fixture();let current=true;
  const a=f.lane.post('analyze-text',{text:'A'},{key:'live',current:()=>current});
  current=false;const b=f.lane.post('generate-forms',{text:'книга'},{key:'word'});
  await f.clock.advance(100);await f.net.finish(0,{message:'rate limited'},429,'3');
  const first=await a;assert.equal(first.stale,true);assert.equal(first.retryAfterMs,3000);
  await f.clock.advance(2999);assert.equal(f.net.calls.length,1);
  await f.clock.advance(1);assert.equal(f.net.calls[1].start,3100);
  await f.net.finish(1);await b;
});

test('HTTP-date Retry-After uses the wall clock while scheduling remains monotonic',async()=>{
  const f=fixture(60),retryDate=new Date(f.clock.wallNow()+5000).toUTCString();
  const a=f.lane.post('analyze-text',{text:'A'});await f.net.finish(0,{message:'busy'},503,retryDate);await a;
  const b=f.lane.post('stress',{text:'A'},{key:'stress'});
  await f.clock.advance(4999);assert.equal(f.net.calls.length,1);
  await f.clock.advance(1);assert.equal(f.net.calls[1].start,5000);
  await f.net.finish(1);await b;
});

test('malformed JSON cannot bypass an observed Retry-After header',async()=>{
  const f=fixture();const a=f.lane.post('analyze-text',{text:'A'});
  const b=f.lane.post('analyze-word',{text:'B'},{key:'word'});
  await f.net.headers(0,429,'5');await f.net.badBody(0);
  assert.ok((await a).error instanceof SyntaxError);
  await f.clock.advance(4999);assert.equal(f.net.calls.length,1,'Cooldown must survive body parse failure.');
  await f.clock.advance(1);assert.equal(f.net.calls[1].start,5000);
  await f.net.finish(1);await b;
});

test('reported HTTP duration includes the parsed body and transport failures do not poll',async()=>{
  const f=fixture(),a=f.lane.post('analyze-text',{text:'A'});
  await f.clock.advance(50);await f.net.headers(0);
  await f.clock.advance(80);await f.net.body(0,{data:{text:'A'}});
  assert.equal((await a).elapsedMs,130);
  const b=f.lane.post('analyze-word',{text:'B'});await f.clock.advance(1895);
  await f.net.fail(1);assert.ok((await b).error instanceof Error);
  await f.clock.advance(60000);assert.equal(f.net.calls.length,2);
});

test('complete reading routes preserve empty support, duplicates, typed rows and indices beyond the display prefix',()=>{
  const typed={raw:{form:'форма',lemma:'Лема',partOfSpeech:'Глагол:raw-tag'},features:{partOfSpeech:'Noun'}};
  const canonical=[typed,typed],emptySupport=[],support=Array.from({length:40},(_,i)=>({raw:{form:'форма',lemma:'Лема'+i,partOfSpeech:'raw'},features:{partOfSpeech:'Noun'},id:i}));
  const data={readingInventory:{occurrences:[{tokenIndex:5,readingSetIndex:2},{tokenIndex:1,readingSetIndex:0},{tokenIndex:3,readingSetIndex:1}],
    sets:[{canonicalReadings:canonical,supportReadings:null},{canonicalReadings:canonical,supportReadings:emptySupport},
      {canonicalReadings:canonical,supportReadings:support,displayedReadingCount:32}]}};
  assert.strictEqual(completeReadings(data,1),canonical);assert.equal(completeReadings(data,1).length,2);
  assert.equal(completeReadings(data,1)[0].features.partOfSpeech,'Noun','Typed features are retained independently of the conflicting raw tag.');
  assert.strictEqual(completeReadings(data,3),emptySupport);assert.equal(completeReadings(data,3).length,0);
  assert.strictEqual(completeReadings(data,5),support);assert.strictEqual(completeReadings(data,5)[35],support[35]);
  assert.equal(completeReadings(data,0),null);assert.equal(completeReadings({},1),null);
});

test('completion preserves emoji/NFD and replaces the entire old suffix only for the exact snapshot',()=>{
  const text='😀 по-ле\u0300сен!\nдруго',caret=9,revision=7;
  const snapshot=completionSpan(text,caret,revision);
  assert.deepEqual(snapshot,{text,caret,start:3,end:12,prefix:'по-ле\u0300',revision});
  const form='по-лѐсен',applied=applyCompletion(text,caret,revision,snapshot,form);
  assert.deepEqual(applied,{text:'😀 по-лѐсен!\nдруго',caret:11});
  assert.equal(applyCompletion(text,caret+1,revision,snapshot,form),null);
  assert.equal(applyCompletion(text+' ',caret,revision,snapshot,form),null);
  assert.equal(applyCompletion(text,caret,revision+1,snapshot,form),null);
  assert.equal(applyCompletion(text,caret,revision,snapshot,null),null);
  assert.equal(completionSpan('😀 ',2,revision),null);
  assert.equal(completionSpan(text,-1,revision),null);assert.equal(completionSpan(text,text.length+1,revision),null);
  assert.equal(caretWord([{kind:'Symbol',start:0,length:2},{kind:'Word',start:3,length:9},{kind:'Punctuation',start:12,length:1}],8),1);
});

class TinyNode {
  constructor(doc,kind,name='',text='') {this.ownerDocument=doc;this.kind=kind;this.nodeName=name;this.children=[];this.value=text;this.dataset={};this.attributes=new Map();this.listeners=new Map();}
  append(...nodes) {for(const node of nodes){if(node.kind==='fragment'){this.append(...node.children);node.children=[];}else this.children.push(node);}}
  replaceChildren(...nodes) {this.children=[];this.value='';this.append(...nodes);}
  get textContent() {return this.value+this.children.map(n=>n.textContent).join('');}
  set textContent(value) {this.children=[];this.value=String(value);}
  set innerHTML(_) {throw new Error('Unsafe HTML sink used.');}
  setAttribute(name,value) {this.attributes.set(name,String(value));}
  getAttribute(name) {return this.attributes.get(name)??null;}
  addEventListener(name,fn) {const callbacks=this.listeners.get(name)??[];callbacks.push(fn);this.listeners.set(name,callbacks);}
  click() {for(const fn of this.listeners.get('click')??[])fn({type:'click'});}
}
function documentFixture() {
  const created=[];
  const doc={created,createDocumentFragment:()=>new TinyNode(doc,'fragment'),
    createTextNode:text=>new TinyNode(doc,'text','#text',text),
    createElement:name=>{created.push(name);return new TinyNode(doc,'element',name);}};
  return {doc,target:new TinyNode(doc,'element','section')};
}

test('safe token DOM preserves raw UTF-16 text/markup/whitespace and original click indices',()=>{
  const {doc,target}=documentFixture(),selected=[];
  const tokens=[{kind:'Symbol',start:0,length:2},{kind:'Word',start:3,length:9},{kind:'Punctuation',start:12,length:1}];
  renderTokenText(target,RAW,tokens,index=>'typed-'+index,index=>selected.push(index));
  assert.equal(target.textContent,RAW);assert.deepEqual(doc.created,['button']);
  const button=target.children.find(n=>n.nodeName==='button');
  assert.equal(button.textContent,'по-ле\u0300сен');assert.equal(button.type,'button');
  assert.equal(button.dataset.tokenIndex,'1');assert.equal(button.getAttribute('aria-pressed'),'false');
  button.click();assert.deepEqual(selected,[1]);
  assert.equal(doc.created.includes('img'),false);
  renderTokenText(target,RAW,[],()=>'',()=>{});
  assert.equal(target.textContent,RAW);
  const attack='<img src=x onerror=alert(1)>';
  renderTokenText(target,attack,[{kind:'Word',start:0,length:attack.length}],()=>'',()=>{});
  assert.equal(target.textContent,attack);assert.equal(doc.created.includes('img'),false);
});

test('invalid or overlapping response spans fail atomically without replacing the previous text',()=>{
  const {target}=documentFixture();target.textContent='предишно';
  assert.throws(()=>renderTokenText(target,'abc',[{kind:'Word',start:2,length:2}],()=>'',()=>{}));
  assert.equal(target.textContent,'предишно');
  assert.throws(()=>renderTokenText(target,'abc',[{kind:'Word',start:0,length:2},{kind:'Word',start:1,length:1}],()=>'',()=>{}));
  assert.equal(target.textContent,'предишно');
});


// The original debounce cases inject a long max-wait only to isolate that oracle.
// These controls independently exercise sustained typing and explicit paused refresh.
test('continuous 100ms typing dispatches current raw snapshots before silence without breaking the shared host cadence',async()=>{
  const clock=clockFixture(),net=transportFixture(clock),snapshots=[],commits=[];
  let latestRaw='';
  const lane=createPostLane({
    fetchImpl:(url,init)=>{
      snapshots.push({at:clock.now(),expectedRaw:latestRaw,postedRaw:JSON.parse(init.body).text});
      return net.fetchImpl(url,init);
    },now:clock.now,wallNow:clock.wallNow,setTimer:clock.setTimer,clearTimer:clock.clearTimer,requestsPerMinute:30
  });
  const controller=createLiveController({lane,commit:(result,revision)=>commits.push({result,revision}),
    invalidate:()=>{},state:()=>{},setTimer:clock.setTimer,clearTimer:clock.clearTimer,debounceMs:150,maxWaitMs:600});
  for(let input=0;input<45;input++){
    if(input)await clock.advance(100);
    latestRaw='  😀 по-ле\u0300сен '+input+'\n';
    controller.edit(latestRaw);
    if(input===0)assert.equal(net.calls.length,0,'Typing starts with a debounce, not immediate fan-out.');
    // Keep the first body outstanding well past max-wait; later completed bodies
    // are also released only after the latest input event has invalidated old UI.
    if(clock.now()>=1400){
      for(let index=0;index<net.calls.length;index++)if(!net.calls[index].finished)
        await net.finish(index,{data:{text:JSON.parse(net.calls[index].init.body).text}});
    }
  }
  const lastInputAt=clock.now();
  assert.ok(net.calls.length>=2,'Multiple snapshots must start while 100ms typing continues for more than four seconds.');
  assert.ok(net.calls[0].start<=600,'Max-wait must prevent debounce starvation during uninterrupted typing.');
  assert.ok(net.calls.every(call=>call.start<lastInputAt),'Observed dispatches precede the end of typing.');
  for(let index=1;index<net.calls.length;index++)
    assert.ok(net.calls[index].start-net.calls[index-1].start>=2025,'All dispatches still obey the current 30RPM transport cadence.');
  for(const snapshot of snapshots)assert.equal(snapshot.postedRaw,snapshot.expectedRaw,'Dispatch must capture the actual newest raw value at that moment.');
  assert.equal(net.maxInFlight,1);assert.equal(controller.text,latestRaw);
  assert.equal(commits.length,0,'Bodies completed after a newer edit cannot commit an old revision.');
  controller.setEnabled(false);
  const count=net.calls.length;await clock.advance(60000);
  assert.equal(net.calls.length,count,'Stopping auto does not leave a background polling loop.');
});

test('manual refresh works while auto is paused, retains IME protection and coalesces through the shared cadence',async()=>{
  const f=liveFixture();f.controller.edit(RAW);f.controller.setEnabled(false);
  await f.clock.advance(5000);assert.equal(f.net.calls.length,0,'Paused auto must not dispatch.');
  f.controller.compositionStart();f.controller.edit('недовършено');f.controller.flush();
  await f.clock.advance(5000);assert.equal(f.net.calls.length,0,'Explicit refresh must not send an unfinished composition.');
  f.controller.compositionEnd(RAW);f.controller.flush();
  assert.equal(f.net.calls.length,1);assert.equal(posted(f.net.calls[0]).text,RAW);
  await f.net.finish(0,{data:{text:RAW}});assert.equal(f.commits.length,1,'The explicit paused result is allowed to commit.');
  f.controller.edit(RAW+' B');f.controller.flush();await f.clock.advance(100);
  f.controller.edit(RAW+' C');f.controller.flush();
  await f.clock.advance(1924);assert.equal(f.net.calls.length,1,'Repeated manual clicks cannot bypass cadence.');
  await f.clock.advance(1);assert.equal(f.net.calls.length,2);
  assert.equal(f.net.calls[1].start-f.net.calls[0].start,2025);
  assert.equal(posted(f.net.calls[1]).text,RAW+' C','Only the latest paused request remains pending.');
  await f.net.finish(1,{data:{text:RAW+' C'}});assert.equal(f.commits.length,2);
  assert.equal(f.net.maxInFlight,1);
  await f.clock.advance(60000);assert.equal(f.net.calls.length,2,'Manual refresh must not re-enable automatic polling.');
});


// Review F1 regression: independent public-record-shaped literals, informed by
// Root's browser observation of zero context roles and three SourceScan syntax
// candidates for “Аз съм добър човек.”. This is a rendering oracle, not a new
// linguistic correctness oracle or an accepted-role/entity count.
const {renderContextSyntaxSummary} = await import(new URL('./live-demo.js',source).href);
const f1ContextEmpty={version:'literal-context',available:true,unavailableReason:null,sentences:[{
  start:0,length:19,complete:true,status:null,selectedPredicateTokenIndex:null,diagnosticPredicateTokenIndex:null,
  candidates:[],localRoles:[],selectedRoles:[],evidenceCodes:[]
}]};
const f1SyntaxThree={version:'literal-syntax',available:true,unavailableReason:null,sentences:[{
  start:0,length:19,status:'Calculated',route:'SourceScan',predicateTokenIndex:1,candidates:[
    {role:'Predicate',ruleId:'BasicPredicate',start:3,length:3,
      targets:[{tokenIndex:1,basis:'ExactCategory',readings:[{tokenIndex:1,completeReadingIndex:0}]}],context:[]},
    {role:'Subject',ruleId:'BasicSubject',start:0,length:2,
      targets:[{tokenIndex:0,basis:'ExactCategory',readings:[{tokenIndex:0,completeReadingIndex:0}]}],
      context:[{tokenIndex:1,basis:'ExactCategory',readings:[{tokenIndex:1,completeReadingIndex:0}]}]},
    {role:'Complement',ruleId:'BasicComplement',start:7,length:11,
      targets:[{tokenIndex:2,basis:'ExactCategory',readings:[{tokenIndex:2,completeReadingIndex:0}]},
        {tokenIndex:3,basis:'ExactCategory',readings:[{tokenIndex:3,completeReadingIndex:0}]}],
      context:[{tokenIndex:1,basis:'ExactCategory',readings:[{tokenIndex:1,completeReadingIndex:0}]}]}
  ]
}],additionalGroups:null}; // Disabled extra heuristics do not disable the available base syntax.
const f1LocalRole={role:'Subject',tokenIndex:0,start:0,length:2,
  readings:[{tokenIndex:0,completeReadingIndex:0}],evidenceCodes:['literal-local']};
const f1SelectedRole={role:'Predicate',tokenIndex:1,start:3,length:3,
  readings:[{tokenIndex:1,completeReadingIndex:0}],evidenceCodes:['literal-selected']};
const f1ContextTwo={...f1ContextEmpty,sentences:[{...f1ContextEmpty.sentences[0],status:'Selected',
  selectedPredicateTokenIndex:1,diagnosticPredicateTokenIndex:1,
  localRoles:[f1LocalRole],selectedRoles:[f1SelectedRole],
  candidates:[{predicateTokenIndex:1,selectable:true,authorityLane:'literal',score:1,canonicalKey:'literal',
    roles:[f1LocalRole,f1SelectedRole],evidenceCodes:['literal-candidate']}]
}]};

function assertF1Summary(context,syntax,expected,name) {
  const {doc,target}=documentFixture();
  renderContextSyntaxSummary(target,context,syntax);
  assert.equal(target.children.length,4,name+': four actual summary/status paragraphs');
  assert.deepEqual(target.children.map(node=>node.nodeName),['p','p','p','p'],name);
  assert.deepEqual(target.children.map(node=>node.textContent),expected,name);
  assert.equal(target.children[0].className,'panel-value',name+': context count is visible summary');
  assert.equal(target.children[2].className,'panel-value',name+': syntax count is visible summary');
  assert.deepEqual(doc.created,['p','p','p','p'],name+': no parsed markup or replacement counters');
}

test('F1 production DOM keeps context zero and syntax three distinct when extra heuristics are null',()=>{
  assertF1Summary(f1ContextEmpty,f1SyntaxThree,[
    'Контекстни роли: 0','Контекст: достъпен.',
    'Синтактични кандидати: 3','Синтаксис: достъпен.'
  ],'observed context0 syntax3');
});

test('F1 production DOM counts local and selected context records without merging syntax authority',()=>{
  assertF1Summary(f1ContextTwo,f1SyntaxThree,[
    'Контекстни роли: 2','Контекст: достъпен.',
    'Синтактични кандидати: 3','Синтаксис: достъпен.'
  ],'context local1+selected1, separate syntax3');
});

test('F1 production DOM preserves independent availability, explicit absent fields and enabled empty evidence',()=>{
  const cases=[
    {name:'context unavailable does not hide available syntax',
      context:{...f1ContextTwo,available:false,unavailableReason:'literal-context-unavailable'},syntax:f1SyntaxThree,
      expected:['Контекстни роли: —','Контекст: недостъпен. literal-context-unavailable','Синтактични кандидати: 3','Синтаксис: достъпен.']},
    {name:'syntax unavailable does not hide available context',context:f1ContextTwo,
      syntax:{...f1SyntaxThree,available:false,unavailableReason:'literal-syntax-unavailable'},
      expected:['Контекстни роли: 2','Контекст: достъпен.','Синтактични кандидати: —','Синтаксис: недостъпен. literal-syntax-unavailable']},
    {name:'null context is not returned, not an inferred disable',context:null,syntax:f1SyntaxThree,
      expected:['Контекстни роли: —','Контекст: не е върнат.','Синтактични кандидати: 3','Синтаксис: достъпен.']},
    {name:'missing context is distinct from null',context:undefined,syntax:f1SyntaxThree,
      expected:['Контекстни роли: —','Контекст: липсва в отговора.','Синтактични кандидати: 3','Синтаксис: достъпен.']},
    {name:'null syntax does not disable context',context:f1ContextTwo,syntax:null,
      expected:['Контекстни роли: 2','Контекст: достъпен.','Синтактични кандидати: —','Синтаксис: не е върнат.']},
    {name:'missing syntax is distinct from null',context:f1ContextTwo,syntax:undefined,
      expected:['Контекстни роли: 2','Контекст: достъпен.','Синтактични кандидати: —','Синтаксис: липсва в отговора.']},
    {name:'available empty evidence has measured zero, not unknown',
      context:{...f1ContextEmpty,sentences:[]},syntax:{...f1SyntaxThree,sentences:[],additionalGroups:[]},
      expected:['Контекстни роли: 0','Контекст: достъпен.','Синтактични кандидати: 0','Синтаксис: достъпен.']},
    {name:'disabled optional groups do not disable available base candidates',context:f1ContextEmpty,syntax:f1SyntaxThree,
      expected:['Контекстни роли: 0','Контекст: достъпен.','Синтактични кандидати: 3','Синтаксис: достъпен.']},
    {name:'unavailable reasons remain literal text',
      context:{...f1ContextEmpty,available:false,unavailableReason:'<img src=x onerror=alert(1)>'},
      syntax:{...f1SyntaxThree,available:false,unavailableReason:null},
      expected:['Контекстни роли: —','Контекст: недостъпен. <img src=x onerror=alert(1)>','Синтактични кандидати: —','Синтаксис: недостъпен.']}
  ];
  for(const row of cases)assertF1Summary(row.context,row.syntax,row.expected,row.name);
});


test('graph authority label follows explicit supports without promoting exact rows or preview roles', async()=>{
  const {graphSupportLabel} = await import(new URL('./live-demo.js',source).href);
  const exact={tokenIndex:1,basis:'ExactCategory',readings:[{tokenIndex:1,completeReadingIndex:3}]};
  const graph={tokenIndex:0,basis:'LicensedGraphMorphology',readings:[],
    graphReadings:[{tokenIndex:0,graphCandidateIndex:0,fullTagIndex:0}]};
  assert.equal(graphSupportLabel([exact]),'');
  assert.equal(graphSupportLabel([{...graph,graphReadings:[]}]),'');
  assert.equal(graphSupportLabel([{...exact,basis:'LicensedGraphMorphology'}]),'');
  assert.equal(graphSupportLabel([exact,graph]),' · Графова опора (хипотеза)');
  assert.equal(graphSupportLabel([{...graph,selectable:false}]),' · Графова опора (хипотеза)');
  assert.equal(graphSupportLabel([]),'');
  assert.deepEqual(exact.readings,[{tokenIndex:1,completeReadingIndex:3}]);
});


// Finite-continuation editor controls. Opaque rows below are synthetic wire fixtures,
// not claims about package-produced tags or the linguistic ranking of a chosen form.
const {renderFiniteContinuation} = await import(new URL('live-demo.js', source).href);
function finiteEvidence(overrides = {}) {
  return {version:'finite_continuation.v1',available:true,unavailableReason:null,
    caret:16,start:13,length:3,prefix:'раб',expectations:[{person:'Third',number:'Singular',supports:[]}],
    suggestions:[{form:'работи',score:10,readings:[{form:'работи',lemma:'работя',partOfSpeech:'opaque fixture row'}],
      matches:[{readingIndex:0,expectationIndices:[0]}]}],truncated:false,searchComplete:true,
    candidateSource:'exact_package_finite_vocabulary',ranking:'dictionary_score',examinedReadings:1,...overrides};
}
function caretFixture(text='Супертрактор раб') {
  const f=fixture(),editor={text,start:text.length,end:text.length},commits=[],invalidations=[];
  const controller=createLiveController({lane:f.lane,
    readSelection:()=>({start:editor.start,end:editor.end}),continuationLimit:12,
    commit:(result,revision,snapshot)=>commits.push({result,revision,snapshot}),
    invalidate:(text,revision)=>invalidations.push({text,revision}),state:()=>{},
    setTimer:f.clock.setTimer,clearTimer:f.clock.clearTimer,debounceMs:150,maxWaitMs:600});
  return {...f,editor,controller,commits,invalidations};
}

test('finite payload is additive and carries caret and form limit inside the one context request',()=>{
  const old=livePayload('Супертрактор раб',false);
  assert.equal(Object.hasOwn(old.context,'continuation'),false);
  const payload=livePayload('Супертрактор раб',false,{caret:16,limit:12});
  assert.deepEqual(payload.context.continuation,{caret:16,limit:12});
  assert.equal(Object.hasOwn(payload,'limit'),false);
  const {continuation,...context}=payload.context;
  assert.deepEqual({...payload,context},old);
});

test('finite insertion after whitespace uses zero length and partial insertion replaces the whole right suffix',()=>{
  const space=continuationSnapshot('Супертрактор ',13,13,5);
  assert.deepEqual(space,{text:'Супертрактор ',caret:13,start:13,end:13,prefix:'',revision:5});
  const empty=bindContinuation(space,finiteEvidence({caret:13,start:13,length:0,prefix:''}));
  assert.deepEqual(applyContinuation('Супертрактор ',13,13,5,empty,'работи'),{text:'Супертрактор работи',caret:19});
  const text='Супертрактор рабXYZ. Следа',snapshot=continuationSnapshot(text,16,16,7);
  assert.deepEqual(snapshot,{text,caret:16,start:13,end:19,prefix:'раб',revision:7});
  const bound=bindContinuation(snapshot,finiteEvidence({length:6}));
  assert.deepEqual(applyContinuation(text,16,16,7,bound,'работи'),{text:'Супертрактор работи. Следа',caret:19});
});

test('finite insertion retains literal emoji and NFD offsets without touching trailing CRLF or another occurrence',()=>{
  const text='😀. Супертрактор раб',snapshot=continuationSnapshot(text,20,20,4);
  assert.deepEqual(snapshot,{text,caret:20,start:17,end:20,prefix:'раб',revision:4});
  const bound=bindContinuation(snapshot,finiteEvidence({caret:20,start:17}));
  assert.deepEqual(applyContinuation(text,20,20,4,bound,'работи'),{text:'😀. Супертрактор работи',caret:23});
  const nfd='и\u0300. Супертрактор рабXYZ.\r\nСупертрактор раб';
  const raw=continuationSnapshot(nfd,20,20,8);
  assert.equal(raw.start,17);assert.equal(raw.end,23);
  const nfdBound=bindContinuation(raw,finiteEvidence({caret:20,start:17,length:6}));
  assert.deepEqual(applyContinuation(nfd,20,20,8,nfdBound,'работи'),
    {text:'и\u0300. Супертрактор работи.\r\nСупертрактор раб',caret:23});
});

test('finite snapshots reject range, invalid UTF16 and interior surrogate combining or emoji cluster boundaries',()=>{
  for(const [text,start,end] of [['abc',1,2],['abc',-1,-1],['abc',4,4],['abc',1.5,1.5],
    ['😀 ',1,1],['и\u0300',1,1],['👩‍💻',2,2],['👩‍💻',3,3]])
    assert.equal(continuationSnapshot(text,start,end,1),null,JSON.stringify({text,start,end}));
  assert.ok(continuationSnapshot('и\u0300',2,2,1));
  assert.ok(continuationSnapshot('😀 ',3,3,1));
});

test('finite binding refuses a server span or source label that does not belong to the captured editable token',()=>{
  const snapshot=continuationSnapshot('Супертрактор раб',16,16,3);
  for(const changes of [{caret:15},{start:12},{length:2},{length:4},{prefix:'ра'},
    {version:'future'}, {candidateSource:'plain_prefix'}, {ranking:'probability'},
    {available:false},{suggestions:null},{suggestions:[{form:''}]}])
    assert.equal(bindContinuation(snapshot,finiteEvidence(changes)),null,JSON.stringify(changes));
  assert.ok(bindContinuation(snapshot,finiteEvidence()));
});

test('finite buttons cannot apply after changed text caret revision selection composition or unsupported form',()=>{
  const text='Супертрактор раб. Супертрактор раб';
  const snapshot=continuationSnapshot(text,16,16,4),bound=bindContinuation(snapshot,finiteEvidence());
  assert.equal(applyContinuation(text.replace('Супертрактор','Супертрактори'),16,16,4,bound,'работи'),null);
  assert.equal(applyContinuation(text,34,34,4,bound,'работи'),null);
  assert.equal(applyContinuation(text,16,16,5,bound,'работи'),null);
  assert.equal(applyContinuation(text,13,16,4,bound,'работи'),null);
  assert.equal(applyContinuation(text,16,16,4,bound,'работи',true),null);
  assert.equal(applyContinuation(text,16,16,4,bound,'работят'),null);
  assert.deepEqual(applyContinuation(text,16,16,4,bound,'работи'),
    {text:'Супертрактор работи. Супертрактор раб',caret:19});
});

test('space and prefix use one analyze-text snapshot rather than a second dictionary request',async()=>{
  const f=caretFixture('Супертрактор ');f.controller.edit(f.editor.text);
  await f.clock.advance(150);assert.equal(f.net.calls.length,1);
  assert.equal(f.net.calls[0].url,'/api/v2/analyze-text');
  assert.deepEqual(posted(f.net.calls[0]).context.continuation,{caret:13,limit:12});
  await f.net.finish(0,{data:{text:f.editor.text,continuation:finiteEvidence({caret:13,start:13,length:0,prefix:''})}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].snapshot.caret,13);
  await f.clock.advance(60000);assert.equal(f.net.calls.length,1);
  f.editor.text='Супертрактор раб';f.editor.start=f.editor.end=16;f.controller.edit(f.editor.text);
  await f.clock.advance(150);assert.equal(f.net.calls.length,2);
  assert.equal(f.net.calls[1].url,'/api/v2/analyze-text');
  assert.deepEqual(posted(f.net.calls[1]).context.continuation,{caret:16,limit:12});
  await f.net.finish(1,{data:{text:f.editor.text,continuation:finiteEvidence()}});
});

test('caret movement alone makes an active result stale and coalesces the next snapshot through host cadence',async()=>{
  const f=caretFixture();f.controller.edit(f.editor.text);f.controller.flush();
  await f.net.headers(0);f.editor.start=f.editor.end=13;
  await f.net.body(0,{data:{text:f.editor.text,continuation:finiteEvidence()}});
  assert.equal(f.commits.length,0,'Guard must observe caret movement even before the DOM event is delivered.');
  f.controller.refreshSelection();const revision=f.controller.revision;
  f.controller.refreshSelection();assert.equal(f.controller.revision,revision,'Repeated selection events do not schedule another revision.');
  await f.clock.advance(2024);assert.equal(f.net.calls.length,1);
  await f.clock.advance(1);assert.equal(f.net.calls.length,2);
  assert.deepEqual(posted(f.net.calls[1]).context.continuation,{caret:13,limit:12});
  await f.net.finish(1,{data:{text:f.editor.text,continuation:finiteEvidence({caret:13,start:13,length:0,prefix:''})}});
  assert.equal(f.commits.length,1);assert.equal(f.net.maxInFlight,1);
});

test('out-of-order callbacks cannot revive an earlier same-text same-caret revision',async()=>{
  const pending=[],commits=[],selection={start:16,end:16};
  const lane={cancel(){},post(operation,payload,options){const d=deferred();pending.push({operation,payload,options,...d});return d.promise;}};
  const controller=createLiveController({lane,readSelection:()=>selection,commit:(result,revision,snapshot)=>commits.push({result,revision,snapshot}),
    invalidate:()=>{},state:()=>{}});
  controller.edit('Супертрактор раб');controller.flush();
  selection.start=selection.end=13;controller.refreshSelection();controller.flush();
  selection.start=selection.end=16;controller.refreshSelection();controller.flush();
  const last=controller.revision;
  pending[2].resolve({stale:false,response:{data:{text:'Супертрактор раб'}}});await drain();
  pending[0].resolve({stale:false,response:{data:{text:'Супертрактор раб'}}});
  pending[1].resolve({stale:false,response:{data:{text:'Супертрактор раб'}}});await drain();
  assert.equal(commits.length,1);assert.equal(commits[0].revision,last);assert.equal(commits[0].snapshot.caret,16);
});

test('range and grapheme-interior selections omit continuation while IME suppresses the entire request until final input',async()=>{
  const f=caretFixture();f.editor.start=13;f.editor.end=16;f.controller.edit(f.editor.text);f.controller.flush();
  assert.equal(Object.hasOwn(posted(f.net.calls[0]).context,'continuation'),false);
  await f.net.finish(0,{data:{text:f.editor.text}});assert.equal(f.commits[0].snapshot,null);
  f.controller.compositionStart();f.editor.text='Супертрактор р';f.editor.start=f.editor.end=14;f.controller.edit(f.editor.text);
  f.controller.flush();await f.clock.advance(5000);assert.equal(f.net.calls.length,1);
  f.editor.text='Супертрактор раб';f.editor.start=f.editor.end=16;f.controller.compositionEnd(f.editor.text);f.controller.flush();
  assert.equal(f.net.calls.length,2);assert.deepEqual(posted(f.net.calls[1]).context.continuation,{caret:16,limit:12});
  await f.net.finish(1,{data:{text:f.editor.text,continuation:finiteEvidence()}});
  f.editor.text='и\u0300. Супертрактор раб';f.editor.start=f.editor.end=1;f.controller.edit(f.editor.text);
  await f.clock.advance(2025);assert.equal(Object.hasOwn(posted(f.net.calls[2]).context,'continuation'),false);
  await f.net.finish(2,{data:{text:f.editor.text}});
});

test('contextual analysis and explicit word actions retain the same single active transport and budget',async()=>{
  const f=caretFixture();f.controller.edit(f.editor.text);f.controller.flush();
  const manual=f.lane.post('analyze-word',{text:'книга'},{key:'word'});
  await f.clock.advance(3000);assert.equal(f.net.calls.length,1);
  await f.net.finish(0,{data:{text:f.editor.text,continuation:finiteEvidence()}});
  assert.equal(f.net.calls.length,2);assert.equal(f.net.calls[1].url,'/api/v2/analyze-word');
  await f.net.finish(1);await manual;
  assert.equal(f.net.maxInFlight,1);assert.ok(f.net.calls[1].start-f.net.calls[0].start>=2025);
  assert.deepEqual(f.net.calls.map(call=>call.url),['/api/v2/analyze-text','/api/v2/analyze-word']);
});

test('finite production DOM preserves complete duplicate rows matches and alternatives while rendering markup as text',()=>{
  const {doc,target}=documentFixture(),chosen=[],attack='<img src=x onerror=alert(1)>',row={form:attack,lemma:'L',partOfSpeech:'opaque raw tag'};
  const evidence=finiteEvidence({expectations:[{person:'Third',number:'Singular',supports:[{tokenIndex:0,readings:[],graphReadings:[{tokenIndex:0,graphCandidateIndex:0,fullTagIndex:0}]}]},
    {person:'Second',number:'Singular',supports:[{tokenIndex:0,readings:[{tokenIndex:0,readingIndex:35}]}]}],
    suggestions:[{form:attack,score:99,readings:[row,row,{...row,lemma:'L2'}],matches:[{readingIndex:2,expectationIndices:[1,0]}]}],truncated:true,searchComplete:false});
  renderFiniteContinuation(target,evidence,continuationSnapshot('Супертрактор раб',16,16,2),(form,bound)=>chosen.push({form,bound}));
  assert.equal(target.hidden,false);assert.equal(doc.created.includes('img'),false);
  const button=target.children.find(node=>node.nodeName==='button');button.click();
  assert.equal(chosen[0].form,attack);assert.deepEqual(chosen[0].bound.forms,[attack]);
  const details=target.children.find(node=>node.nodeName==='details');
  const saved=JSON.parse(details.children.find(node=>node.nodeName==='pre').textContent);
  assert.deepEqual(saved,{readings:[row,row,{...row,lemma:'L2'}],matches:[{readingIndex:2,expectationIndices:[1,0]}]});
  assert.match(target.textContent,/Third.*Singular/);assert.match(target.textContent,/Second.*Singular/);
  assert.match(target.textContent,/речников score, не вероятност/);assert.match(target.textContent,/извън показания лимит/);
  assert.match(target.textContent,/Изчерпаността на търсенето не е удостоверена/);
  assert.match(target.textContent,/вдясно от курсора не е проверена/);
});

test('finite production DOM distinguishes unavailable context from exhausted empty and incomplete empty searches',()=>{
  const snapshot=continuationSnapshot('Супертрактор раб',16,16,1),f=documentFixture();
  renderFiniteContinuation(f.target,finiteEvidence({available:false,unavailableReason:'unsupported context'}),snapshot,()=>assert.fail());
  assert.match(f.target.textContent,/unsupported context/);assert.equal(f.target.children.some(node=>node.nodeName==='button'),false);
  renderFiniteContinuation(f.target,finiteEvidence({suggestions:[],searchComplete:true,truncated:false}),snapshot,()=>assert.fail());
  assert.match(f.target.textContent,/обхват е изчерпано/);assert.match(f.target.textContent,/Няма върнати съвместими форми/);
  renderFiniteContinuation(f.target,finiteEvidence({suggestions:[],searchComplete:false,truncated:false}),snapshot,()=>assert.fail());
  assert.match(f.target.textContent,/Изчерпаността на търсенето не е удостоверена/);
  assert.doesNotMatch(f.target.textContent,/обхват е изчерпано/);
  renderFiniteContinuation(f.target,undefined,snapshot,()=>assert.fail());assert.equal(f.target.hidden,true);
});

// Append after the existing 36 tests. Provisional seams are documented in patch-plan.json.
// No runtime, syntax check, or tests were executed by this helper.
function nominalEvidence(overrides = {}) {
  return {version:'nominal_continuation.v1',target:'Noun',available:true,unavailableReason:null,
    caret:6,start:5,length:1,prefix:'к',expectations:[],
    nominalExpectations:[{number:'Singular',gender:'Feminine',firstAttributeArticle:'Indefinite',
      nounArticle:'Indefinite',attributes:[{role:'Attribute',tokenIndex:0,start:0,length:4,
        readings:[{tokenIndex:0,completeReadingIndex:0}],graphReadings:null}]}],
    suggestions:[{form:'книга',score:9,readings:[{form:'книга',lemma:'книга',fullTag:'literal client fixture'}],
      matches:[{readingIndex:0,expectationIndices:[0]}]}],
    truncated:false,searchComplete:true,candidateSource:'exact_package_nominal_vocabulary',
    ranking:'dictionary_score',examinedReadings:1,...overrides};
}
function targetFixture(text='Нова к', target='Auto') {
  const f=fixture(),editor={text,start:text.length,end:text.length,target},commits=[],invalidations=[];
  const controller=createLiveController({lane:f.lane,
    readSelection:()=>({start:editor.start,end:editor.end}),
    readContinuationTarget:()=>editor.target,continuationLimit:12,
    commit:(result,revision,snapshot)=>commits.push({result,revision,snapshot}),
    invalidate:(text,revision)=>invalidations.push({text,revision}),state:()=>{},
    setTimer:f.clock.setTimer,clearTimer:f.clock.clearTimer,debounceMs:150,maxWaitMs:600});
  return {...f,editor,controller,commits,invalidations};
}

test('target payload preserves the legacy finite shape and carries an explicit selected enum name',()=>{
  const text='Нова к',legacy=livePayload(text,false,{caret:6,limit:12});
  assert.deepEqual(legacy.context.continuation,{caret:6,limit:12});
  for(const target of ['Finite','Noun','Auto']){
    const payload=livePayload(text,false,{caret:6,limit:12,target});
    assert.deepEqual(payload.context.continuation,{caret:6,limit:12,target});
    assert.equal(Object.hasOwn(payload,'limit'),false);
    const {continuation,...context}=payload.context;
    assert.deepEqual(context,livePayload(text,false).context);
  }
});

test('configured Live Auto uses exactly one analyze-text request and records the requested target',async()=>{
  const f=targetFixture();f.controller.edit(f.editor.text);await f.clock.advance(150);
  assert.equal(f.net.calls.length,1);assert.equal(f.net.calls[0].url,'/api/v2/analyze-text');
  assert.deepEqual(posted(f.net.calls[0]).context.continuation,{caret:6,limit:12,target:'Auto'});
  await f.net.finish(0,{data:{text:f.editor.text,continuation:nominalEvidence()}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].snapshot.requestTarget,'Auto');
  await f.clock.advance(60000);assert.equal(f.net.calls.length,1);assert.equal(f.net.maxInFlight,1);
});

test('target changes coalesce queued work to only the newest target without another endpoint',async()=>{
  const f=targetFixture();f.controller.edit(f.editor.text);const first=f.controller.revision;
  await f.clock.advance(50);f.editor.target='Finite';f.controller.refreshTarget();
  await f.clock.advance(50);f.editor.target='Noun';f.controller.refreshTarget();
  assert.ok(f.controller.revision>first);
  await f.clock.advance(149);assert.equal(f.net.calls.length,0);
  await f.clock.advance(1);assert.equal(f.net.calls.length,1);
  assert.equal(posted(f.net.calls[0]).context.continuation.target,'Noun');
  assert.equal(f.net.calls[0].url,'/api/v2/analyze-text');
  await f.net.finish(0,{data:{text:f.editor.text,continuation:nominalEvidence()}});
  assert.equal(f.commits.length,1);assert.equal(f.commits[0].snapshot.requestTarget,'Noun');
});

test('a changed target stales an active body even before its DOM change event arrives',async()=>{
  const f=targetFixture();f.controller.edit(f.editor.text);f.controller.flush();
  await f.net.headers(0);f.editor.target='Noun'; // Listener has not run.
  await f.net.body(0,{data:{text:f.editor.text,continuation:nominalEvidence()}});
  assert.equal(f.commits.length,0);f.controller.refreshTarget();
  await f.clock.advance(2024);assert.equal(f.net.calls.length,1);
  await f.clock.advance(1);assert.equal(f.net.calls.length,2);
  assert.deepEqual(posted(f.net.calls[1]).context.continuation,{caret:6,limit:12,target:'Noun'});
  await f.net.finish(1,{data:{text:f.editor.text,continuation:nominalEvidence()}});
  assert.equal(f.commits.length,1);assert.equal(f.net.maxInFlight,1);
});

test('out-of-order Auto Noun Auto responses cannot revive an old matching mode snapshot',async()=>{
  const pending=[],commits=[],selection={start:6,end:6};let target='Auto';
  const lane={cancel(){},post(operation,payload,options){const d=deferred();pending.push({operation,payload,options,...d});return d.promise;}};
  const controller=createLiveController({lane,readSelection:()=>selection,readContinuationTarget:()=>target,
    commit:(result,revision,snapshot)=>commits.push({result,revision,snapshot}),invalidate:()=>{},state:()=>{}});
  controller.edit('Нова к');controller.flush();
  target='Noun';controller.refreshTarget();controller.flush();
  target='Auto';controller.refreshTarget();controller.flush();
  assert.deepEqual(pending.map(x=>x.payload.context.continuation.target),['Auto','Noun','Auto']);
  const finalRevision=controller.revision;
  pending[2].resolve({stale:false,response:{data:{text:'Нова к',continuation:nominalEvidence()}}});await drain();
  pending[0].resolve({stale:false,response:{data:{text:'Нова к',continuation:nominalEvidence()}}});
  pending[1].resolve({stale:false,response:{data:{text:'Нова к',continuation:nominalEvidence()}}});await drain();
  assert.equal(commits.length,1);assert.equal(commits[0].revision,finalRevision);
  assert.equal(commits[0].snapshot.requestTarget,'Auto');
});

test('target changes during composition send no intermediate request and keep the final chosen mode',async()=>{
  const f=targetFixture();f.controller.edit(f.editor.text);f.controller.compositionStart();
  f.editor.target='Noun';f.controller.refreshTarget();f.controller.flush();
  await f.clock.advance(5000);assert.equal(f.net.calls.length,0);
  f.editor.text='Нова кни';f.editor.start=f.editor.end=f.editor.text.length;
  f.controller.compositionEnd(f.editor.text);f.controller.flush();
  assert.equal(f.net.calls.length,1);
  assert.deepEqual(posted(f.net.calls[0]).context.continuation,{caret:8,limit:12,target:'Noun'});
  await f.net.finish(0,{data:{text:f.editor.text,continuation:nominalEvidence({caret:8,length:3,prefix:'кни'})}});
  assert.equal(f.commits.length,1);
});

test('nominal insertion keeps zero-length whitespace and original Unicode whole-word suffix boundaries',()=>{
  const space=continuationSnapshot('Нова ',5,5,4,'Noun');
  const empty=bindContinuation(space,nominalEvidence({caret:5,start:5,length:0,prefix:''}));
  assert.deepEqual(applyContinuation('Нова ',5,5,4,empty,'книга',false,'Noun'),{text:'Нова книга',caret:10});
  for(const prefix of ['😀. ','и\u0300. ']){
    const text=prefix+'Нова кнXYZ лежат.\r\n',snapshot=continuationSnapshot(text,10,10,7,'Noun');
    assert.equal(snapshot.start,9);assert.equal(snapshot.end,14);assert.equal(snapshot.prefix,'к');
    const bound=bindContinuation(snapshot,nominalEvidence({caret:10,start:9,length:5,prefix:'к'}));
    assert.deepEqual(applyContinuation(text,10,10,7,bound,'книга',false,'Noun'),
      {text:prefix+'Нова книга лежат.\r\n',caret:14});
  }
});

test('binding enforces the selected lane and its sole expectation-index domain',()=>{
  const noun=continuationSnapshot('Нова к',6,6,2,'Noun');
  assert.ok(bindContinuation(noun,nominalEvidence()));
  for(const changes of [
    {target:'Finite'},{target:'Auto'},{version:'finite_continuation.v1'},
    {candidateSource:'exact_package_finite_vocabulary'},{nominalExpectations:null},
    {expectations:[{person:'Third',number:'Singular',supports:[]}]},
    {suggestions:[{form:'книга',score:1,readings:[{}],matches:[{readingIndex:0,expectationIndices:[1]}]}]},
    {suggestions:[{form:'книга',score:1,readings:[{}],matches:[{readingIndex:0,expectationIndices:[-1]}]}]},
    {suggestions:[{form:'книга',score:1,readings:[{}],matches:[{readingIndex:1,expectationIndices:[0]}]}]}
  ])assert.equal(bindContinuation(noun,nominalEvidence(changes)),null,JSON.stringify(changes));
  const finite=continuationSnapshot('Супертрактор раб',16,16,2,'Finite');
  assert.ok(bindContinuation(finite,finiteEvidence()));
  assert.equal(bindContinuation(finite,finiteEvidence({nominalExpectations:[{}]})),null);
});

test('requested Auto may bind an actual noun lane while explicit targets cannot cross lanes',()=>{
  const auto=continuationSnapshot('Нова к',6,6,2,'Auto'),bound=bindContinuation(auto,nominalEvidence());
  assert.ok(bound);
  assert.deepEqual(applyContinuation('Нова к',6,6,2,bound,'книга',false,'Auto'),{text:'Нова книга',caret:10});
  assert.equal(applyContinuation('Нова к',6,6,2,bound,'книга',false,'Noun'),null);
  const finite=continuationSnapshot('Супертрактор раб',16,16,2,'Finite');
  assert.equal(bindContinuation(finite,nominalEvidence({caret:16,start:13,length:3,prefix:'раб'})),null);
  const noun=continuationSnapshot('Нова к',6,6,2,'Noun');
  assert.equal(bindContinuation(noun,finiteEvidence({caret:6,start:5,length:1,prefix:'к'})),null);
  assert.equal(applyContinuation('Нова к',6,6,3,bound,'книга',false,'Auto'),null);
  assert.equal(applyContinuation('Нова к',5,6,2,bound,'книга',false,'Auto'),null);
  assert.equal(applyContinuation('Нова к',6,6,2,bound,'книга',true,'Auto'),null);
});

test('nominal production DOM shows the actual noun lane and preserves article branches raw readings and refs',()=>{
  const {doc,target}=documentFixture(),chosen=[],attack='<img src=x onerror=alert(1)>';
  const readings=[{form:'книга',lemma:'книга',fullTag:'noun fixture'}, {form:'книга',lemma:'книга',fullTag:'noun fixture'},
    {form:attack,lemma:'other',fullTag:'other-POS fixture'}];
  const attribute={role:'Attribute',tokenIndex:0,start:0,length:4,readings:[{tokenIndex:0,completeReadingIndex:7}],
    graphReadings:[{tokenIndex:0,graphCandidateIndex:2,fullTagIndex:1}]};
  const expectations=[
    {number:'Singular',gender:'Masculine',firstAttributeArticle:'Definite',nounArticle:'Indefinite',attributes:[attribute]},
    {number:'Singular',gender:'Masculine',firstAttributeArticle:'Full',nounArticle:'Indefinite',attributes:[attribute]}];
  const evidence=nominalEvidence({nominalExpectations:expectations,suggestions:[{form:'книга',score:9,readings,
    matches:[{readingIndex:0,expectationIndices:[0,1]},{readingIndex:1,expectationIndices:[0,1]}]}]});
  renderFiniteContinuation(target,evidence,continuationSnapshot('Нова к',6,6,2,'Auto'),(form,bound)=>chosen.push({form,bound}));
  assert.equal(target.hidden,false);assert.match(target.textContent,/Съществителни/);
  assert.match(target.textContent,/Singular/);assert.match(target.textContent,/Masculine/);
  assert.match(target.textContent,/Definite/);assert.match(target.textContent,/Full/);assert.match(target.textContent,/Indefinite/);
  assert.match(target.textContent,/exact refs: 1/);assert.match(target.textContent,/graph refs: 1/);
  const paragraphs=target.children.filter(n=>n.nodeName==='p').map(n=>n.textContent);
  assert.ok(paragraphs.some(text=>text.includes('Definite')&&!text.includes('Full')&&!text.includes('Short')),
    'Generic definiteness must not be relabeled Full or Short.');
  const details=target.children.find(n=>n.nodeName==='details');
  const preserved=JSON.parse(details.children.find(n=>n.nodeName==='pre').textContent);
  assert.deepEqual(preserved.readings,readings);assert.deepEqual(preserved.matches,evidence.suggestions[0].matches);
  assert.equal(doc.created.includes('img'),false);
  target.children.find(n=>n.nodeName==='button').click();
  assert.equal(chosen.length,1);assert.equal(chosen[0].form,'книга');
  assert.match(target.textContent,/вдясно от курсора не е проверена/);
});

test('nominal production DOM keeps unavailable and complete empty scopes distinct without finite subject claims',()=>{
  const snapshot=continuationSnapshot('Нова к',6,6,1,'Noun'),f=documentFixture();
  renderFiniteContinuation(f.target,nominalEvidence({available:false,unavailableReason:'nominal_context_unavailable'}),snapshot,()=>assert.fail());
  assert.equal(f.target.hidden,false);assert.match(f.target.textContent,/nominal_context_unavailable/);
  assert.equal(f.target.children.some(n=>n.nodeName==='button'),false);
  renderFiniteContinuation(f.target,nominalEvidence({suggestions:[],searchComplete:true}),snapshot,()=>assert.fail());
  assert.match(f.target.textContent,/обхват е изчерпано/);assert.match(f.target.textContent,/Няма върнати съвместими форми/);
  const visible=f.target.children.filter(n=>n.nodeName==='p').map(n=>n.textContent).join(' ');
  assert.doesNotMatch(visible,/Subject|Third|First|Second/);
  renderFiniteContinuation(f.target,nominalEvidence({suggestions:[],searchComplete:false}),snapshot,()=>assert.fail());
  assert.match(f.target.textContent,/Изчерпаността на търсенето не е удостоверена/);
  assert.doesNotMatch(f.target.textContent,/обхват е изчерпано/);
});

test('the actual static page exposes Auto verb and noun modes outside the hidden result panel',async()=>{
  const {readFile}=await import('node:fs/promises'),html=await readFile(new URL('demo.html',source),'utf8');
  const select=html.match(/<select\b[^>]*\bid="live-continuation-target"[^>]*>([\s\S]*?)<\/select>/);
  assert.ok(select,'Expose the target selector before any continuation result exists.');
  const options=[...select[1].matchAll(/<option\b([^>]*)>([^<]*)<\/option>/g)].map(m=>({
    value:m[1].match(/\bvalue="([^"]+)"/)?.[1],label:m[2].trim(),selected:/\bselected\b/.test(m[1])}));
  assert.deepEqual(options.map(x=>[x.value,x.label]),[['Auto','Авто'],['Finite','Глаголи'],['Noun','Съществителни']]);
  assert.equal(options.slice(1).some(x=>x.selected),false,'The page default is Auto.');
  const result=html.match(/<section\b[^>]*\bid="live-completion"[^>]*>[\s\S]*?<\/section>/);
  assert.ok(result);assert.equal(result[0].includes('live-continuation-target'),false);
  const help=html.match(/<[^>]+\bid="live-continuation-help"[^>]*>([\s\S]*?)<\/[^>]+>/);
  assert.ok(help);assert.match(help[1],/глагол|finite|финитн/iu);
  assert.match(help[1],/съществителн/iu);assert.match(help[1],/приоритет|предпочит/iu);
});
