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
  constructor(doc,kind,name='',text='') {
    this.ownerDocument=doc;this.kind=kind;this.nodeName=name;this.children=[];this.value=text;
    this.parentNode=null;this.dataset={};this.attributes=new Map();this.listeners=new Map();
  }
  get nodeType() {return this.kind==='text'?3:this.kind==='fragment'?11:1;}
  get childNodes() {return this.children;}
  get firstChild() {return this.children[0]??null;}
  get nextSibling() {const rows=this.parentNode?.childNodes,index=rows?.indexOf(this);return rows&&index>=0?rows[index+1]??null:null;}
  get data() {return this.value;}
  set data(value) {this.value=String(value);}
  get nodeValue() {return this.kind==='text'?this.value:null;}
  set nodeValue(value) {if(this.kind==='text')this.value=String(value);}
  append(...nodes) {for(const node of nodes)this.insertBefore(node,null);}
  insertBefore(node,reference) {
    if(node.kind==='fragment'){for(const child of [...node.childNodes])this.insertBefore(child,reference);return node;}
    if(node===reference)return node;
    if(reference!==null&&!this.children.includes(reference))throw Error('Fixture reference is not a child.');
    node.parentNode?.removeChild(node);
    const index=reference===null?this.children.length:this.children.indexOf(reference);
    this.children.splice(index,0,node);node.parentNode=this;return node;
  }
  removeChild(node) {
    const index=this.children.indexOf(node);if(index<0)throw Error('Fixture node is not a child.');
    this.children.splice(index,1);node.parentNode=null;return node;
  }
  remove() {this.parentNode?.removeChild(this);}
  contains(node) {return node===this||this.childNodes.some(child=>child.contains(node));}
  get isConnected() {return this.ownerDocument.documentElement?.contains(this)??false;}
  replaceChildren(...nodes) {for(const node of [...this.children])this.removeChild(node);this.append(...nodes);}
  get textContent() {return this.kind==='text'?this.value:this.children.map(node=>node.textContent).join('');}
  set textContent(value) {
    const text=value==null?'':String(value);
    if(this.kind==='text'){this.value=text;return;}
    this.replaceChildren(...(text?[this.ownerDocument.createTextNode(text)]:[]));
  }
  set innerHTML(_) {throw new Error('Unsafe HTML sink used.');}
  setAttribute(name,value) {this.attributes.set(name,String(value));}
  getAttribute(name) {return this.attributes.get(name)??null;}
  addEventListener(name,fn) {const callbacks=this.listeners.get(name)??[];callbacks.push(fn);this.listeners.set(name,callbacks);}
  click() {if(this.disabled)return;for(const fn of this.listeners.get('click')??[])fn({type:'click',target:this,currentTarget:this});}
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


// Independent presentation oracles. These are synthetic public-wire fixtures,
// not assertions about package-produced morphology, warning coverage or accuracy.
const {diagnosticPresentation} = await import(new URL('live-demo.js', source).href);
const CHECK_TOKEN = {kind:'Word',start:3,length:4};
const CHECK_WARNING = {code:'pronoun_verb_agreement_mismatch',start:3,length:4,ruleId:'literal-agreement'};
const CHECK_UNSUPPORTED = {code:'full_short_article_check_not_ready',start:3,length:4,ruleId:null};
const CHECK_INFORMATION = {code:'reviewed_word_formation_evidence',start:3,length:4,ruleId:'literal-morphology'};
function checksWire(diagnostics = [], validation = {available:true,diagnostics:[]}) {
  return {text:'Аз чете.',tokens:[{kind:'Word',start:0,length:2},CHECK_TOKEN,{kind:'Punctuation',start:7,length:1}],diagnostics,validation};
}

test('diagnostic presentation warns only for the eleven explicit supported warning codes',()=>{
  const codes=['missing_terminal_punctuation','sentence_initial_lowercase','mixed_script_ocr_known_candidate',
    'digit_shape_ocr_known_candidate','accent_sensitive_i_grave','spelling_unknown_word_unique_distance_one_candidate',
    'pronoun_verb_agreement_mismatch','preposition_missing_complement','full_article_expected_for_subject_candidate',
    'productive_morphology_agreement_mismatch','productive_attribute_agreement_mismatch'];
  for(const code of codes){
    const diagnostic=Object.freeze({...CHECK_WARNING,code});
    const actual=diagnosticPresentation(checksWire([diagnostic]),CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-warning',code);
    assert.equal(actual.messages[0].classification,'warning',code);
    assert.equal(actual.messages[0].label,'Предупреждение',code);
    assert.strictEqual(actual.messages[0].diagnostic,diagnostic,'Original diagnostic identity is preserved.');
  }
});

test('not-ready article check is unsupported coverage rather than an agreement warning',()=>{
  const actual=diagnosticPresentation(checksWire([CHECK_UNSUPPORTED]),CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-unsupported-check');
  assert.equal(actual.messages[0].classification,'unsupported-check');
  assert.equal(actual.messages[0].text,'Неподдържана проверка · full_short_article_check_not_ready · 3+4');
});

test('reviewed word-formation evidence is information with its original exact reading support',()=>{
  const readings=Object.freeze([{tokenIndex:1,completeReadingIndex:3}]);
  const diagnostic=Object.freeze({...CHECK_INFORMATION,readings});
  const actual=diagnosticPresentation(checksWire([diagnostic]),CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-information');
  assert.equal(actual.messages[0].classification,'information');
  assert.strictEqual(actual.messages[0].diagnostic.readings,readings);
  assert.equal(actual.messages[0].text,'Информация · reviewed_word_formation_evidence · 3+4');
});

test('hyphenated degree morphology remains informational evidence',()=>{
  const actual=diagnosticPresentation(checksWire([{...CHECK_INFORMATION,code:'hyphenated_degree_morphology_evidence'}]),CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-information');
  assert.equal(actual.messages[0].text,'Информация · hyphenated_degree_morphology_evidence · 3+4');
});

test('unknown and mismatch-looking future codes retain identity as information',()=>{
  for(const code of ['future_diagnostic','future_agreement_mismatch','mismatch_retained_for_diagnostic']){
    const actual=diagnosticPresentation(checksWire([{...CHECK_INFORMATION,code}]),CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-information',code);
    assert.equal(actual.messages[0].classification,'information',code);
    assert.equal(actual.messages[0].diagnostic.code,code);
  }
});

test('overlapping warning unsupported and information use strongest class without dropping any message',()=>{
  const w=CHECK_WARNING,u=CHECK_UNSUPPORTED,i=CHECK_INFORMATION;
  for(const rows of [[w,u,i],[w,i,u],[u,w,i],[u,i,w],[i,w,u],[i,u,w]]){
    const actual=diagnosticPresentation(checksWire(rows),CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-warning');
    assert.equal(actual.messages.length,3);
    assert.deepEqual(actual.messages.map(row=>row.diagnostic),rows,'Keep original message order and all lower classes.');
    assert.deepEqual(actual.messages.map(row=>row.label).sort(),['Информация','Неподдържана проверка','Предупреждение'].sort());
  }
});

test('unsupported outranks information and multiple informational messages stay visible',()=>{
  for(const rows of [[CHECK_UNSUPPORTED,CHECK_INFORMATION],[CHECK_INFORMATION,CHECK_UNSUPPORTED]]){
    const actual=diagnosticPresentation(checksWire(rows),CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-unsupported-check');assert.equal(actual.messages.length,2);
  }
  const actual=diagnosticPresentation(checksWire([CHECK_INFORMATION,{...CHECK_INFORMATION,code:'future'}]),CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-information');assert.equal(actual.messages.length,2);
});

test('measured empty checks are neutral and explicitly do not certify grammar or unavailable coverage',()=>{
  const unavailableChecks=Object.freeze(['literal-unimplemented-check']);
  const data=checksWire([],{available:true,diagnostics:[],spacingDiagnostics:null,unavailableChecks});
  const actual=diagnosticPresentation(data,CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-empty');assert.equal(actual.messages.length,0);
  assert.equal(actual.coverage,'Кандидатни проверки в ограничения обхват; липсата на съобщение не удостоверява граматична правилност.');
  assert.strictEqual(data.validation.unavailableChecks,unavailableChecks,'Presentation does not consume or erase coverage details.');
});

test('null missing and unavailable checks retain distinct coverage messages and a neutral unavailable class',()=>{
  const absent=checksWire();delete absent.validation;
  const cases=[
    [checksWire([],null),'Проверките не са върнати.'],
    [absent,'Проверките липсват в отговора.'],
    [checksWire([],{available:false,unavailableReason:'literal-offline'}),'Проверките са недостъпни. literal-offline']
  ];
  for(const [data,coverage]of cases){
    const actual=diagnosticPresentation(data,CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-unavailable');assert.equal(actual.coverage,coverage);assert.equal(actual.messages.length,0);
  }
});

test('returned top-level warning remains visible when validation coverage is absent or unavailable',()=>{
  for(const validation of [null,undefined,{available:false,unavailableReason:'literal-unavailable'}]){
    const data=checksWire([CHECK_WARNING]);
    if(validation===undefined)delete data.validation;else data.validation=validation;
    const actual=diagnosticPresentation(data,CHECK_TOKEN);
    assert.equal(actual.tokenClass,'check-warning');
    assert.strictEqual(actual.messages[0].diagnostic,CHECK_WARNING);
    assert.notEqual(actual.coverage,'Кандидатни проверки в ограничения обхват; липсата на съобщение не удостоверява граматична правилност.');
  }
});

test('all five actual diagnostic arrays contribute messages without merging original source records',()=>{
  const rows=[CHECK_WARNING,CHECK_UNSUPPORTED,CHECK_INFORMATION,
    {...CHECK_INFORMATION,code:'hyphenated_degree_morphology_evidence'}, {...CHECK_INFORMATION,code:'future-fragment'}];
  const data=checksWire([rows[0]],{available:true,diagnostics:[rows[1]],spacingDiagnostics:[rows[2]],
    subordinateCommaDiagnostics:[rows[3]],fragmentDiagnostics:[rows[4]]});
  const actual=diagnosticPresentation(data,CHECK_TOKEN);
  assert.deepEqual(actual.messages.map(row=>row.diagnostic),rows);assert.equal(actual.tokenClass,'check-warning');
  for(let index=0;index<rows.length;index++)assert.strictEqual(actual.messages[index].diagnostic,rows[index]);
});

test('diagnostic token DOM preserves emoji NFD repeated words and exact UTF-16 target identity',()=>{
  const text='😀 и\u0300 Аз чете. Аз чете.';
  const tokens=[{kind:'Symbol',start:0,length:2},{kind:'Word',start:3,length:2},{kind:'Word',start:6,length:2},
    {kind:'Word',start:9,length:4},{kind:'Punctuation',start:13,length:1},{kind:'Word',start:15,length:2},
    {kind:'Word',start:18,length:4},{kind:'Punctuation',start:22,length:1}];
  const data={...checksWire([{...CHECK_WARNING,start:18}]),text,tokens};
  const {doc,target}=documentFixture(),selected=[];
  renderTokenText(target,text,tokens,index=>diagnosticPresentation(data,tokens[index]).tokenClass,index=>selected.push(index));
  assert.equal(target.textContent,text);assert.equal(text.length,23);
  const buttons=target.children.filter(node=>node.nodeName==='button');
  assert.equal(buttons[2].textContent,'чете');assert.equal(buttons[2].className,'live-token check-empty');
  assert.equal(buttons.at(-1).textContent,'чете');assert.equal(buttons.at(-1).className,'live-token check-warning');
  assert.equal(buttons.at(-1).dataset.tokenIndex,'6');buttons.at(-1).click();assert.deepEqual(selected,[6]);
  assert.equal(doc.created.includes('img'),false);
});

test('zero-length boundary diagnostics remain messages without underlining either adjacent word',()=>{
  const data={...checksWire([{...CHECK_WARNING,code:'missing_terminal_punctuation',start:7,length:0}]),text:'Аз чета.'};
  const first=diagnosticPresentation(data,data.tokens[0]),last=diagnosticPresentation(data,CHECK_TOKEN);
  assert.equal(first.tokenClass,'check-empty');assert.equal(last.tokenClass,'check-empty');
  assert.equal(last.messages[0].text,'Предупреждение · missing_terminal_punctuation · 7+0');
});

test('context preview mismatch evidence alone cannot create an actual warning',()=>{
  const data=checksWire();data.context={sentences:[{candidates:[{selectable:false,
    evidenceCodes:['mismatch_retained_for_diagnostic','productive_morphology_agreement_mismatch']}]}]};
  const actual=diagnosticPresentation(data,CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-empty');assert.deepEqual(actual.messages,[]);
});

test('diagnostic labels keep unsafe-looking data literal and graph authority follows explicit supports',()=>{
  const code='<img src=x onerror=alert(1)>',graphReadings=Object.freeze([{tokenIndex:1,graphCandidateIndex:0,fullTagIndex:0}]);
  const diagnostic=Object.freeze({...CHECK_INFORMATION,code,graphReadings});
  const actual=diagnosticPresentation(checksWire([diagnostic],{available:false,unavailableReason:code}),CHECK_TOKEN);
  assert.equal(actual.tokenClass,'check-information');assert.strictEqual(actual.messages[0].diagnostic.graphReadings,graphReadings);
  assert.equal(actual.messages[0].text,'Информация · '+code+' · 3+4 · Графова опора (хипотеза)');
  assert.equal(actual.coverage,'Проверките са недостъпни. '+code);
  const {doc,target}=documentFixture(),row=doc.createElement('p');row.textContent=actual.messages[0].text;target.append(row);
  assert.equal(target.textContent,actual.messages[0].text);assert.deepEqual(doc.created,['p']);
});

test('late diagnostic classes cannot revive old caret revisions and composition still sends only its final input',async()=>{
  const pending=[],views=[],selection={start:8,end:8};
  const lane={cancel(){},post(operation,payload,options){const d=deferred();pending.push({operation,payload,options,...d});return d.promise;}};
  const controller=createLiveController({lane,readSelection:()=>selection,
    commit:result=>views.push(diagnosticPresentation(result.response.data,CHECK_TOKEN)),invalidate:()=>{},state:()=>{}});
  controller.edit('Аз чете.');controller.flush();
  selection.start=selection.end=0;controller.refreshSelection();controller.flush();
  selection.start=selection.end=8;controller.refreshSelection();controller.flush();
  assert.equal(pending.length,3);
  pending[2].resolve({stale:false,response:{data:checksWire()}});await drain();
  pending[0].resolve({stale:false,response:{data:checksWire([CHECK_WARNING])}});
  pending[1].resolve({stale:false,response:{data:checksWire([CHECK_UNSUPPORTED])}});await drain();
  assert.equal(views.length,1);assert.equal(views[0].tokenClass,'check-empty');
  controller.compositionStart();controller.edit('Аз чете.');controller.flush();assert.equal(pending.length,3);
  controller.compositionEnd('Аз чете.');controller.flush();assert.equal(pending.length,4);
  pending[3].resolve({stale:false,response:{data:checksWire([CHECK_WARNING])}});await drain();
  assert.equal(views.length,2);assert.equal(views[1].tokenClass,'check-warning');controller.setEnabled(false);
});


// Real initLiveDemo lifecycle fixture. Extend the existing tiny DOM; no browser
// engine is simulated. Real-browser focus/scroll/IME evidence remains separate.
const {initLiveDemo} = await import(new URL('live-demo.js', source).href);
class LiveLifecycleNode extends TinyNode {
  constructor(doc,kind,name='',text='') {
    super(doc,kind,name,text);this.hidden=false;this.disabled=false;this.open=false;
    this.scrollTop=0;this.scrollLeft=0;this.selectionStart=0;this.selectionEnd=0;this.checked=false;
    this.className='';this.id='';this.setRangeTextCalls=[];
  }
  record(operation,before) {
    this.ownerDocument.mutations?.push({target:this,operation,before,after:super.textContent});
  }
  insertBefore(node,reference) {const before=super.textContent,result=super.insertBefore(node,reference);this.record('insertBefore',before);return result;}
  removeChild(node) {
    const before=super.textContent;
    if(node.contains?.(this.ownerDocument.activeElement)||node===this.ownerDocument.activeElement)this.ownerDocument.activeElement=this.ownerDocument.body;
    const result=super.removeChild(node);this.record('removeChild',before);return result;
  }
  replaceChildren(...nodes) {const before=super.textContent;super.replaceChildren(...nodes);this.record('replaceChildren',before);}
  get textContent() {return super.textContent;}
  set textContent(value) {const before=super.textContent;super.textContent=value;this.record('textContent',before);}
  get isConnected() {return this.ownerDocument.documentElement?.contains(this)??false;}
  contains(node) {return node===this||this.childNodes.some(child=>child.contains?.(node)??child===node);}
  get classList() {
    const tokens=()=>new Set(this.className.split(/\s+/).filter(Boolean));
    return {contains:name=>tokens().has(name),add:(...names)=>{this.className=[...new Set([...tokens(),...names])].join(' ');},
      remove:(...names)=>{this.className=[...tokens()].filter(name=>!names.includes(name)).join(' ');},
      toggle:(name,force)=>{const set=tokens(),enabled=force??!set.has(name);if(enabled)set.add(name);else set.delete(name);this.className=[...set].join(' ');return enabled;}};
  }
  setAttribute(name,value) {
    super.setAttribute(name,value);
    if(name==='class')this.className=String(value);
    if(name==='id')this.id=String(value);
    if(name.startsWith('data-'))this.dataset[name.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]=String(value);
    if(['disabled','hidden','inert'].includes(name))this[name]=true;
  }
  getAttribute(name) {
    if(name==='class')return this.className||null;if(name==='id')return this.id||null;
    if(name.startsWith('data-'))return this.dataset[name.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())]??null;
    return super.getAttribute(name);
  }
  removeAttribute(name) {
    this.attributes.delete(name);
    if(name.startsWith('data-'))delete this.dataset[name.slice(5).replace(/-([a-z])/g,(_,letter)=>letter.toUpperCase())];
    if(['disabled','hidden','inert'].includes(name))this[name]=false;
  }
  hasAttribute(name) {return this.getAttribute(name)!==null;}
  toggleAttribute(name,force) {const enabled=force??!this.hasAttribute(name);if(enabled)this.setAttribute(name,'');else this.removeAttribute(name);return enabled;}
  matches(selector) {
    return selector.split(',').some(part=>{
      const s=part.trim();
      if(s.startsWith('#'))return this.id===s.slice(1);
      if(s.startsWith('.'))return this.className.split(/\s+/).includes(s.slice(1));
      const attribute=/^\[([^=\]]+)(?:=["']?([^"'\]]+)["']?)?\]$/.exec(s);
      if(attribute)return attribute[2]===undefined?this.hasAttribute(attribute[1]):this.getAttribute(attribute[1])===attribute[2];
      return this.nodeName.toLowerCase()===s.toLowerCase();
    });
  }
  querySelectorAll(selector) {
    const result=[];
    const visit=node=>{for(const child of node.childNodes){if(child.nodeType===1&&child.matches(selector))result.push(child);visit(child);}};
    visit(this);return result;
  }
  querySelector(selector) {return this.querySelectorAll(selector)[0]??null;}
  closest(selector) {for(let node=this;node;node=node.parentNode)if(node.nodeType===1&&node.matches?.(selector))return node;return null;}
  dispatch(type,extra={}) {
    if(type==='click'&&this.disabled&&!extra.force)return false;
    const event={type,target:this,currentTarget:this,defaultPrevented:false,
      preventDefault(){this.defaultPrevented=true;},...extra};
    for(const fn of [...(this.listeners.get(type)??[])])fn(event);
    if(typeof this['on'+type]==='function')this['on'+type](event);
    return !event.defaultPrevented;
  }
  dispatchEvent(event) {return this.dispatch(event.type,event);}
  click() {return this.dispatch('click');}
  focus() {if(!this.disabled&&this.isConnected)this.ownerDocument.activeElement=this;}
  blur() {if(this.ownerDocument.activeElement===this)this.ownerDocument.activeElement=this.ownerDocument.body;}
  setSelectionRange(start,end) {this.selectionStart=start;this.selectionEnd=end;}
  setRangeText(form,start,end,mode='preserve') {
    const oldStart=this.selectionStart,oldEnd=this.selectionEnd,delta=form.length-(end-start);
    this.setRangeTextCalls.push({form,start,end,mode});this.value=this.value.slice(0,start)+form+this.value.slice(end);
    if(mode==='end')this.selectionStart=this.selectionEnd=start+form.length;
    else if(mode==='start')this.selectionStart=this.selectionEnd=start;
    else if(mode==='select'){this.selectionStart=start;this.selectionEnd=start+form.length;}
    else {
      this.selectionStart=oldStart>end?oldStart+delta:oldStart>start?start:oldStart;
      this.selectionEnd=oldEnd>end?oldEnd+delta:oldEnd>start?start+form.length:oldEnd;
    }
  }
  scrollIntoView(options) {this.lastScrollIntoView=options;}
}
function liveLifecycleDocument() {
  const doc={created:[],mutations:[],activeElement:null};
  doc.createDocumentFragment=()=>new LiveLifecycleNode(doc,'fragment');
  doc.createTextNode=text=>new LiveLifecycleNode(doc,'text','#text',text);
  doc.createElement=name=>{doc.created.push(name);return new LiveLifecycleNode(doc,'element',name);};
  doc.documentElement=doc.createElement('html');doc.body=doc.createElement('body');doc.documentElement.append(doc.body);doc.activeElement=doc.body;
  const add=(id,tag='div',parent=doc.body)=>{const node=doc.createElement(tag);node.id=id;parent.append(node);return node;};
  const nodes=new Map();
  const tags={'live-text':'textarea','live-auto':'input','live-heuristics':'input','live-layer':'select','live-continuation-target':'select',
    'live-json':'pre','live-refresh':'button','live-stress':'button','theme-toggle':'button','inspect-title':'h3'};
  for(const id of ['live-text','live-preview','live-inspector','live-auto','live-heuristics','live-layer','live-state','live-panels',
    'live-action-result','live-completion','live-spelling','live-continuation-target','live-timing','live-word-count','live-reading-count',
    'live-length','live-legend','inspect-title','live-refresh','live-stress','live-budget','theme-toggle'])nodes.set('#'+id,add(id,tags[id]??'div'));
  const rawDetails=add('fixture-raw-details','details');nodes.set('#live-json',add('live-json','pre',rawDetails));
  const secondary=add('fixture-secondary','details');secondary.className='secondary-workspace';nodes.set('.secondary-workspace',secondary);
  nodes.set('#explorer',add('explorer','details',secondary));
  const inspectorHost=add('fixture-inspector-host','aside');inspectorHost.className='inspector';
  inspectorHost.append(nodes.get('#inspect-title'),nodes.get('#live-inspector'));
  nodes.get('#live-state').append(doc.createElement('i'),doc.createElement('span'));
  doc.querySelector=selector=>nodes.get(selector)??doc.documentElement.querySelector(selector);
  doc.querySelectorAll=selector=>selector==='[data-live-example]'?[]:doc.documentElement.querySelectorAll(selector);
  nodes.get('#live-auto').checked=true;nodes.get('#live-heuristics').checked=true;
  nodes.get('#live-layer').value='pos';nodes.get('#live-continuation-target').value='Auto';
  nodes.get('#live-completion').hidden=true;nodes.get('#live-spelling').hidden=true;nodes.get('#live-action-result').hidden=true;
  nodes.get('#inspect-title').textContent='Текущата дума';
  return {doc,nodes,rawDetails};
}
function lifecycleReply(text,{search='literal-search-1',leadingSymbol=false,continuation=null}={}) {
  // Explicit wire-only projection. This fixture supplies opaque reading evidence,
  // never a grammar/corpus oracle or a fallback language engine.
  const tokens=[...text.matchAll(/[\p{L}\p{M}-]+/gu)].map(match=>({kind:'Word',text:match[0],start:match.index,length:match[0].length,analysis:{isKnown:true}}));
  if(leadingSymbol)tokens.unshift({kind:'Symbol',text:text.slice(0,2),start:0,length:2});
  const occurrences=[],sets=[],words=[];
  tokens.forEach((token,tokenIndex)=>{if(token.kind!=='Word')return;
    occurrences.push({tokenIndex,readingSetIndex:sets.length});
    sets.push({canonicalReadings:[{raw:{form:token.text,lemma:token.text.toLowerCase(),partOfSpeech:'literal-tag'},
      features:{partOfSpeech:'Noun',number:'Singular'}}],supportReadings:null});
    words.push({tokenIndex,fullReadingCount:1,observedProjectedAmbiguity:false,actualDisplayTruncated:false,issues:[]});});
  return {data:{text,tokens,readingInventory:{occurrences,sets},profile:{fullReadingOccurrences:occurrences.length,words},
    diagnostics:[],lexical:{tokens:[]},context:{available:true,sentences:[]},syntax:{available:true,sentences:[]},
    validation:{available:true,diagnostics:[],spacingDiagnostics:null,subordinateCommaDiagnostics:null,fragmentDiagnostics:null,unavailableChecks:[]},
    spelling:{words:tokens.map((token,tokenIndex)=>({tokenIndex,search,alternatives:[]})).filter(row=>tokens[row.tokenIndex].kind==='Word')},
    properNames:{candidates:[]},wordConstructions:null,phonetics:null,metrics:{},continuation}};
}
async function withLiveLifecycle(body,{text='Котката спи.',caret=2}={}) {
  const f=fixture(),dom=liveLifecycleDocument(),saved=new Map(),searches=[],details=[];
  const globals={document:dom.doc,localStorage:{getItem:()=>null,setItem(){}},matchMedia:()=>({matches:false}),
    performance:{now:f.clock.now},setTimeout:f.clock.setTimer,clearTimeout:f.clock.clearTimer};
  for(const [name,value]of Object.entries(globals)){saved.set(name,Object.getOwnPropertyDescriptor(globalThis,name));Object.defineProperty(globalThis,name,{configurable:true,writable:true,value});}
  const get=selector=>dom.doc.querySelector(selector),editor=get('#live-text');editor.value=text;editor.setSelectionRange(caret,caret);editor.focus();
  const nativeHeaders=f.net.headers.bind(f.net);f.net.headers=async(index,...args)=>{f.net.calls[index].lifecycleHeaders=true;await nativeHeaders(index,...args);};
  const view={...f,...dom,get,editor,preview:get('#live-preview'),inspector:get('#live-inspector'),panels:get('#live-panels'),
    title:get('#inspect-title'),completion:get('#live-completion'),spelling:get('#live-spelling'),
    scrollHost:get('#live-inspector').closest('.inspector'),actionHost:get('#live-action-result'),searches,details,
    type(value,position=value.length){editor.value=value;editor.setSelectionRange(position,position);editor.dispatch('input');},
    caret(position,key='ArrowRight'){editor.setSelectionRange(position,position);editor.dispatch('keyup',{key});},
    forceClick(node){node.dispatch('click',{force:true});},
    words(){return Array.from(get('#live-preview').querySelectorAll('[data-token-index]'));},
    refresh(){get('#live-refresh').click();},
    async accept(index,reply=lifecycleReply(editor.value)){await f.net.finish(index,reply);},
    async first(reply=lifecycleReply(editor.value)){await f.clock.advance(150);assert.equal(f.net.calls.length,1);await f.net.finish(0,reply);}
  };
  try {
    view.app=initLiveDemo({lane:f.lane,tokenAnalysis:token=>token.analysis??null,
      showTokenDetails:(token,raw,response)=>details.push({token,raw,response}),search:(operation,raw)=>searches.push({operation,raw})});
    view.app.metadata({limits:{requestsPerMinutePerIp:30}});
    await body(view);
  } finally {
    try {
      get('#live-auto').checked=false;get('#live-auto').dispatch('change');f.lane.cancel('manual');
      for(let index=0;index<f.net.calls.length;index++)if(!f.net.calls[index].finished){
        if(f.net.calls[index].lifecycleHeaders)await f.net.badBody(index,new Error('Fixture disposal'));
        else await f.net.fail(index,new Error('Fixture disposal'));
      }
      await drain();
    } finally {
      for(const [name,descriptor]of saved)if(descriptor)Object.defineProperty(globalThis,name,descriptor);else delete globalThis[name];
    }
  }
}


// Prospective actual-init regressions. Server data below is an opaque public-wire
// presentation fixture; it does not claim linguistic correctness or browser IME.
test('actual Live keeps the last complete frame through sustained typing and shared-lane max-wait', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();
    const words=f.words(),panelNodes=[...f.panels.childNodes],inspectorNodes=[...f.inspector.childNodes];
    const checkpoint=f.doc.mutations.length;
    for(let index=0;index<40;index++){
      if(index)await f.clock.advance(100);
      f.type('Котката спи '+index);
      assert.equal(f.preview.textContent,'Котката спи.','The editor changes while the displayed analysis remains explicitly old.');
      assert.match(f.title.textContent,/Котката/);assert.doesNotMatch(f.title.textContent,/Текущата дума/);
      assert.equal(f.preview.dataset.fresh,'false');
      assert.ok(words.every(node=>f.preview.contains(node)));assert.ok(panelNodes.every(node=>f.panels.contains(node)));
      assert.ok(inspectorNodes.every(node=>f.inspector.contains(node)));
    }
    assert.ok(f.net.calls.length>=2,'An actual analyze-text request starts during sustained typing, before silence.');
    assert.ok(f.net.calls[1].start<f.clock.now());assert.equal(f.net.maxInFlight,1);
    for(let index=1;index<f.net.calls.length;index++)assert.ok(f.net.calls[index].start-f.net.calls[index-1].start>=2025);
    const destructive=f.doc.mutations.slice(checkpoint).filter(row=>[f.preview,f.inspector,f.panels].includes(row.target)&&row.after==='');
    assert.deepEqual(destructive,[],'No blank analysis/panel/inspector frames are produced by edit invalidation.');
  });
});

test('actual Live caret-only selection uses the still-valid analysis without rebuilding the same occurrence', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();
    const wordNodes=f.words(),first=f.inspector.firstChild,reading=f.inspector.querySelector('details'),forms=reading.querySelector('button');
    reading.open=true;reading.dispatch('toggle');f.scrollHost.scrollTop=73;f.scrollHost.scrollLeft=9;forms.focus();
    f.caret(3);
    assert.strictEqual(f.inspector.firstChild,first);assert.strictEqual(f.inspector.querySelector('details'),reading);
    assert.equal(reading.open,true);assert.strictEqual(f.doc.activeElement,forms);assert.equal(f.scrollHost.scrollTop,73);assert.equal(f.scrollHost.scrollLeft,9);
    assert.match(f.title.textContent,/Котката/);assert.ok(wordNodes.every(node=>f.preview.contains(node)));
    f.caret(9);
    assert.match(f.title.textContent,/спи/,'Another occurrence is selected locally before a network reply.');
    assert.doesNotMatch(f.title.textContent,/Текущата дума/);assert.equal(f.preview.textContent,'Котката спи.');
    assert.ok(wordNodes.every(node=>f.preview.contains(node)));assert.equal(f.completion.hidden,true);
  });
});

test('actual Live refreshes changed data for the same occurrence while retaining semantic details scroll and focus', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();
    const oldWord=f.words()[0],reading=f.inspector.querySelector('details'),forms=reading.querySelector('button');
    reading.open=true;reading.dispatch('toggle');f.scrollHost.scrollTop=61;f.scrollHost.scrollLeft=7;forms.focus();
    assert.match(f.inspector.textContent,/literal-search-1/);
    f.refresh();await f.clock.advance(2025);assert.equal(f.net.calls.length,2);
    await f.accept(1,lifecycleReply(f.editor.value,{search:'literal-search-2'}));
    assert.strictEqual(f.words()[0],oldWord,'Unchanged source occurrence keeps its word button.');
    assert.match(f.inspector.textContent,/literal-search-2/);assert.doesNotMatch(f.inspector.textContent,/literal-search-1/);
    assert.equal(f.inspector.querySelector('details').open,true);assert.equal(f.scrollHost.scrollTop,61);assert.equal(f.scrollHost.scrollLeft,7);
    assert.ok(f.inspector.contains(f.doc.activeElement));assert.equal(f.doc.activeElement.textContent,'Словоформи за тази лема');
  });
});

test('actual Live keyed word nodes refresh token-index and click bindings after a valid response plan changes', {concurrency:false}, async()=>{
  const raw='😀 и\u0300 Котка спи';
  await withLiveLifecycle(async f=>{
    await f.first(lifecycleReply(raw));
    const old=f.words().find(node=>node.textContent==='Котка');assert.equal(old.dataset.tokenIndex,'1');
    old.focus();f.refresh();await f.clock.advance(2025);
    await f.accept(1,lifecycleReply(raw,{leadingSymbol:true}));
    const current=f.words().find(node=>node.textContent==='Котка');
    assert.strictEqual(current,old);assert.equal(current.dataset.tokenIndex,'2');
    current.click();assert.match(f.title.textContent,/Котка/);
    assert.equal(f.editor.selectionStart,6);assert.equal(f.editor.selectionEnd,11);assert.equal(f.preview.textContent,raw);
    assert.equal(current.getAttribute('aria-pressed'),'true');
  },{text:raw,caret:8});
});

test('actual Live edit immediately disables displayed old actions and forced old callbacks cannot act on new text', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();
    const oldWord=f.words()[0],oldActions=Array.from(f.inspector.querySelectorAll('button'));
    assert.ok(oldActions.length>=2);
    f.type('Котката спят.');const selection=[f.editor.selectionStart,f.editor.selectionEnd];
    assert.equal(f.get('#live-stress').disabled,true);assert.ok(oldActions.every(button=>button.disabled));
    for(const button of oldActions)f.forceClick(button);
    f.forceClick(oldWord);f.forceClick(f.get('#live-stress'));
    assert.deepEqual(f.searches,[]);assert.deepEqual(f.details,[]);assert.deepEqual([f.editor.selectionStart,f.editor.selectionEnd],selection);
    assert.equal(f.actionHost.hidden,true);assert.equal(f.preview.dataset.fresh,'false');
    await f.clock.advance(2025);
    assert.ok(f.net.calls.every(call=>call.url==='/api/v2/analyze-text'),'No queued old stress/word action starts later.');
  });
});

test('actual Live caret and IME changes invalidate old continuation callbacks without blanking displayed analysis', {concurrency:false}, async()=>{
  const raw='Котката раб';
  await withLiveLifecycle(async f=>{
    await f.first(lifecycleReply(raw,{continuation:finiteEvidence({caret:11,start:8,length:3,prefix:'раб'})}));
    const button=f.completion.querySelector('button');assert.ok(button);assert.equal(f.completion.hidden,false);
    f.editor.setSelectionRange(10,10);f.forceClick(button);
    assert.equal(f.editor.value,raw);assert.equal(f.editor.setRangeTextCalls.length,0,'Raw caret movement is checked even before its event.');
    f.caret(9);assert.equal(f.completion.hidden,true);assert.equal(f.preview.textContent,raw);f.forceClick(button);
    assert.equal(f.editor.setRangeTextCalls.length,0);
    f.editor.dispatch('compositionstart');f.type(raw+'о');f.forceClick(button);await f.clock.advance(5000);
    assert.equal(f.net.calls.length,1);assert.equal(f.editor.setRangeTextCalls.length,0);assert.equal(f.preview.textContent,raw);
    f.editor.dispatch('compositionend');await f.clock.advance(150);
    assert.equal(f.net.calls.length,2);assert.equal(posted(f.net.calls[1]).text,raw+'о');
    f.forceClick(button);assert.equal(f.editor.setRangeTextCalls.length,0);
  },{text:raw,caret:11});
});

test('actual Live stale malformed 429 keeps cooldown and cannot restore old actions or a blank frame', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();const frame=[...f.preview.childNodes];
    f.type('Котката спят.',10);await f.clock.advance(2025);assert.equal(f.net.calls.length,2);
    f.type('Котката спят тук.',10);
    await f.net.headers(1,429,'3');await f.net.badBody(1);
    assert.equal(f.preview.textContent,'Котката спи.');assert.ok(frame.every(node=>f.preview.contains(node)));
    assert.equal(f.get('#live-stress').disabled,true);assert.equal(f.preview.dataset.fresh,'false');
    await f.clock.advance(2999);assert.equal(f.net.calls.length,2);
    await f.clock.advance(1);assert.equal(f.net.calls.length,3);assert.equal(posted(f.net.calls[2]).text,'Котката спят тук.');
    await f.accept(2);assert.equal(f.preview.textContent,'Котката спят тук.');assert.equal(f.preview.dataset.fresh,'true');
  });
});

test('actual Live rejects invalid response spans atomically and keeps the last good display stale/error', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();const frame=[...f.preview.childNodes],inspector=f.inspector.textContent,panels=[...f.panels.childNodes];
    f.type('Котката спят.',10);await f.clock.advance(2025);
    const bad=lifecycleReply(f.editor.value);bad.data.tokens[1]={...bad.data.tokens[1],start:2};
    await f.accept(1,bad);
    assert.equal(f.preview.textContent,'Котката спи.');assert.deepEqual([...f.preview.childNodes],frame);
    assert.equal(f.inspector.textContent,inspector);assert.ok(panels.every(node=>f.panels.contains(node)));
    assert.match(f.title.textContent,/Котката/);assert.equal(f.preview.dataset.fresh,'false');
    assert.equal(f.get('#live-state').dataset.state,'error');assert.equal(f.get('#live-stress').disabled,true);
  });
});

test('actual Live placeholders return only for an explicit clear and old controls remain inert', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    await f.first();const action=f.inspector.querySelector('button');
    f.type('');assert.equal(f.preview.textContent,'');assert.equal(f.words().length,0);assert.equal(f.completion.hidden,true);
    assert.equal(f.spelling.hidden,true);assert.doesNotMatch(f.title.textContent,/Котката/);
    f.forceClick(action);assert.deepEqual(f.searches,[]);await f.clock.advance(5000);assert.equal(f.net.calls.length,1);
  });
});

test('actual Live preserves literal HTML emoji and NFD through layer/theme changes without reparsing token nodes', {concurrency:false}, async()=>{
  const raw='😀 по-ле\u0300сен <img src=x onerror=alert(1)>';
  await withLiveLifecycle(async f=>{
    await f.first(lifecycleReply(raw));const words=f.words();
    assert.equal(f.preview.textContent,raw);assert.equal(f.editor.value,raw);assert.ok(words.some(node=>node.textContent==='по-ле\u0300сен'));
    assert.equal(f.doc.created.includes('img'),false);
    f.get('#live-layer').value='checks';f.get('#live-layer').dispatch('change');f.get('#theme-toggle').click();
    assert.equal(f.preview.textContent,raw);assert.ok(words.every(node=>f.preview.contains(node)));assert.equal(f.doc.created.includes('img'),false);
    f.rawDetails.open=true;f.rawDetails.dispatch('toggle');assert.equal(JSON.parse(f.get('#live-json').textContent).data.text,raw);
  },{text:raw,caret:9});
});

function lifecycleMisspellingReply(contextual=true) {
  const reply=lifecycleReply('Това е друфа дума'),word=reply.data.spelling.words.find(row=>row.tokenIndex===2);
  reply.data.tokens[2].analysis={isKnown:false};reply.data.readingInventory.sets[2].canonicalReadings=[];
  word.alternatives=[{form:'друга',distance:1},{form:'друма',distance:1}];
  if(contextual)word.contextualCandidates=[{alternativeIndex:0,assessment:'Supported',matches:[{candidateReadingIndex:0,followingNounReading:{tokenIndex:3,completeReadingIndex:0}}]},{alternativeIndex:1,assessment:'Unassessed',matches:[]}];
  return reply;
}

test('actual Live chooses an earlier spelling candidate by captured word span while caret remains at text end', {concurrency:false}, async()=>{
  const raw='Това е друфа дума';
  await withLiveLifecycle(async f=>{
    const reply=lifecycleMisspellingReply();assert.equal(reply.data.tokens[2].start,7);assert.equal(reply.data.tokens[2].length,5);
    await f.first(reply);const candidate=Array.from(f.spelling.querySelectorAll('button')).find(node=>node.textContent.includes('друга'));
    assert.ok(candidate);assert.equal(f.editor.selectionStart,17);assert.equal(f.spelling.hidden,false);
    candidate.click();assert.equal(f.editor.value,'Това е друга дума');assert.equal(f.editor.selectionStart,17);assert.equal(f.editor.selectionEnd,17);
    assert.deepEqual(f.editor.setRangeTextCalls.at(-1),{form:'друга',start:7,end:12,mode:'preserve'});
    assert.equal(candidate.disabled,true);assert.equal(f.spelling.hidden,false,'Previous candidate evidence stays visible, explicitly stale.');
    f.forceClick(candidate);assert.equal(f.editor.setRangeTextCalls.length,1);await f.clock.advance(2025);
    assert.equal(posted(f.net.calls[1]).text,'Това е друга дума');
  },{text:raw,caret:17});
});

test('actual Live spelling callbacks veto pre-event caret changes edit and IME while stale evidence stays visible', {concurrency:false}, async()=>{
  const raw='Това е друфа дума';
  await withLiveLifecycle(async f=>{
    await f.first(lifecycleMisspellingReply());const candidate=Array.from(f.spelling.querySelectorAll('button')).find(node=>node.textContent.includes('друга'));
    assert.ok(candidate);f.editor.setSelectionRange(13,13);f.forceClick(candidate);
    assert.equal(f.editor.value,raw);assert.equal(f.editor.setRangeTextCalls.length,0);
    f.editor.dispatch('compositionstart');f.forceClick(candidate);await f.clock.advance(5000);
    assert.equal(f.net.calls.length,1);assert.equal(f.spelling.hidden,false);assert.equal(candidate.disabled,true);assert.equal(f.editor.value,raw);
    f.editor.dispatch('compositionend');f.type('Това е друга дума');f.forceClick(candidate);
    assert.equal(f.editor.setRangeTextCalls.length,0);assert.equal(f.spelling.hidden,false);assert.equal(candidate.disabled,true);
  },{text:raw,caret:17});
});

test('actual Live spelling surface keeps raw alternatives when contextual assessment metadata is absent', {concurrency:false}, async()=>{
  await withLiveLifecycle(async f=>{
    const reply=lifecycleMisspellingReply(false);await f.first(reply);
    assert.equal(f.spelling.hidden,false);assert.match(f.spelling.textContent,/друга/);assert.match(f.spelling.textContent,/друма/);
    assert.deepEqual(reply.data.spelling.words[2].alternatives.map(row=>row.distance),[1,1]);
    assert.equal(f.editor.value,'Това е друфа дума');assert.equal(f.editor.setRangeTextCalls.length,0,'Rendering never automatically corrects text.');
  },{text:'Това е друфа дума',caret:17});
});


// Five opaque UI passthrough fixtures. The distance1 values below are synthetic
// server fields, not computed Levenshtein facts or package witnesses for these5
// forms. Only друга is separately covered by the primary package-bound oracle.
const LIFECYCLE_RAW_FIVE_FORMS=['друга','друма','opaque-form-3','opaque-form-4','opaque-form-5'];
function lifecycleContextSpellingReply(text,assessment='Supported',{rawFive=false}={}) {
  const reply=lifecycleReply(text),typo=reply.data.tokens.findIndex(token=>token.text==='друфа'),noun=reply.data.tokens.findIndex(token=>token.text==='дума'||token.text==='дом');
  reply.data.tokens[typo].analysis={isKnown:false};
  const occurrence=reply.data.readingInventory.occurrences.find(row=>row.tokenIndex===typo);
  reply.data.readingInventory.sets[occurrence.readingSetIndex].canonicalReadings=[];
  const word=reply.data.spelling.words.find(row=>row.tokenIndex===typo);
  word.alternatives=(rawFive?LIFECYCLE_RAW_FIVE_FORMS:['друга','друма']).map(form=>({form,distance:1}));
  word.contextualCandidates=[{alternativeIndex:0,assessment,matches:assessment.toLowerCase()==='supported'
    ?[{candidateReadingIndex:0,followingNounReading:{tokenIndex:noun,completeReadingIndex:0}}]:[]},
    ...word.alternatives.slice(1).map((_,index)=>({alternativeIndex:index+1,assessment:'Unassessed',matches:[]}))];
  return reply;
}

test('actual Live qualifies supported spelling readings and preserves all5 raw distance fields for Pascal and camel assessment', {concurrency:false}, async()=>{
  for(const assessment of ['Supported','supported'])await withLiveLifecycle(async f=>{
    const reply=lifecycleContextSpellingReply('Това е друфа дума',assessment,{rawFive:true});await f.first(reply);
    assert.match(f.spelling.textContent,/съвместим прочит с „дума“/,'The label qualifies an attested compatible reading, not a unique correction.');
    for(const form of LIFECYCLE_RAW_FIVE_FORMS)assert.ok(f.spelling.textContent.includes(form),'Raw form remains visible: '+form);
    assert.ok((f.spelling.textContent.match(/разстояние\s*1/g)??[]).length>=5,'Each raw distance1 remains visible; contextual assessment does not replace the metric.');
    assert.deepEqual(reply.data.spelling.words[2].alternatives.map(row=>row.distance),[1,1,1,1,1]);
    assert.equal(f.editor.value,'Това е друфа дума');assert.equal(f.editor.setRangeTextCalls.length,0,'Rendering and qualification never automatically edit the text.');
  },{text:'Това е друфа дума',caret:17});
});

test('actual Live updates spelling support when only the following noun changes for the same selected typo occurrence', {concurrency:false}, async()=>{
  for(const assessment of ['NoCompatiblePair','Unassessed'])await withLiveLifecycle(async f=>{
    await f.first(lifecycleContextSpellingReply('Това е друфа дума'));
    const typo=f.words().find(node=>node.textContent==='друфа'),details=f.inspector.querySelector('details');
    assert.ok(typo);assert.ok(details);details.open=true;details.dispatch('toggle');f.scrollHost.scrollTop=47;
    assert.match(f.title.textContent,/друфа/);assert.match(f.spelling.textContent,/съвместим прочит с „дума“/);
    f.type('Това е друфа дом',9);assert.equal(f.editor.selectionStart,9);await f.clock.advance(2025);
    const next=lifecycleContextSpellingReply('Това е друфа дом',assessment);
    assert.equal(next.data.tokens[2].start,7);assert.equal(next.data.tokens[2].length,5);assert.equal(next.data.tokens[3].text,'дом');
    await f.accept(1,next);
    assert.strictEqual(f.words().find(node=>node.textContent==='друфа'),typo,'The unchanged source span retains its token node.');
    assert.match(f.title.textContent,/друфа/);assert.equal(f.inspector.querySelector('details').open,true);assert.equal(f.scrollHost.scrollTop,47);
    assert.doesNotMatch(f.spelling.textContent,/съвместим прочит с „дума“/,'A previous Supported badge must not be cached by typo span/form alone.');
    assert.equal(f.editor.value,'Това е друфа дом');assert.equal(f.editor.setRangeTextCalls.length,0);
  },{text:'Това е друфа дума',caret:9});
});

test('actual Live spelling replacement preserves earlier UTF16 spans after emoji and NFD prefixes at end caret', {concurrency:false}, async()=>{
  const cases=[
    {raw:'😀 Това е друфа дума',start:10,nounStart:16,caret:20,expected:'😀 Това е друга дума'},
    {raw:'и\u0300. Това е друфа дума',start:11,nounStart:17,caret:21,expected:'и\u0300. Това е друга дума'}
  ];
  for(const row of cases)await withLiveLifecycle(async f=>{
    const reply=lifecycleContextSpellingReply(row.raw),typo=reply.data.tokens.find(token=>token.text==='друфа'),noun=reply.data.tokens.find(token=>token.text==='дума');
    assert.equal(row.raw.length,row.caret);assert.equal(typo.start,row.start);assert.equal(typo.length,5);assert.equal(noun.start,row.nounStart);assert.equal(noun.length,4);
    await f.first(reply);const candidate=Array.from(f.spelling.querySelectorAll('button')).find(node=>node.textContent.includes('друга'));
    assert.ok(candidate);assert.equal(f.editor.selectionStart,row.caret);candidate.click();
    assert.equal(f.editor.value,row.expected);assert.equal(f.editor.selectionStart,row.caret);assert.equal(f.editor.selectionEnd,row.caret);
    assert.deepEqual(f.editor.setRangeTextCalls.at(-1),{form:'друга',start:row.start,end:row.start+5,mode:'preserve'});
    assert.ok(f.editor.value.endsWith('друга дума'));assert.equal(f.doc.created.includes('img'),false);
  },{text:row.raw,caret:row.caret});
});

// Opaque UI presentation cases; no package availability, edit-distance or tokenizer oracle.
const SPELLING_CASE_LITERAL_ROWS = [
  {"id":"lower","source":"друфа","candidate":"друга","expected":"друга"},
  {"id":"title","source":"Друфа","candidate":"друга","expected":"Друга"},
  {"id":"caps","source":"ДРУФА","candidate":"друга","expected":"ДРУГА"},
  {"id":"mixed-source","source":"ДрУфа","candidate":"друга","expected":"друга"},
  {"id":"proper-dictionary-case","source":"ДРУФА","candidate":"Аруба","expected":"Аруба"},
  {"id":"mixed-dictionary-case","source":"ДРУФА","candidate":"дРуга","expected":"дРуга"},
  {"id":"already-caps-candidate","source":"Друфа","candidate":"БАН","expected":"БАН"},
  {"id":"unsupported-latin-source","source":"Drufa","candidate":"друга","expected":"друга"},
  {"id":"unsupported-latin-candidate","source":"ДРУФА","candidate":"other","expected":"other"},
  {"id":"unsupported-source-digit","source":"Друфа2","candidate":"друга","expected":"друга"},
  {"id":"unsupported-hyphen-candidate","source":"ДРУФА","candidate":"по-друга","expected":"по-друга"},
  {"id":"ambiguous-single-upper","source":"Д","candidate":"друга","expected":"друга"},
  {"id":"internal-mark-title","source":"Др̀уфа","candidate":"др̀уга","expected":"Др̀уга"},
  {"id":"internal-mark-caps","source":"ДР̀УФА","candidate":"др̀уга","expected":"ДР̀УГА"},
  {"id":"NFD-not-composed","source":"Ѝскра","candidate":"ѝскра","expected":"Ѝскра"},
  {"id":"cased-mark-copied-not-uppercased","source":"ДРУФА","candidate":"дрͅуга","expected":"ДРͅУГА"},
  {"id":"leading-mark-source-unsupported","source":"̀Друфа","candidate":"друга","expected":"друга"},
  {"id":"lower-source-preserves-candidate-mark","source":"друфа","candidate":"дру́га","expected":"дру́га"}
];

const SPELLING_CASE_INTEGRATION_ROWS = [
  {"id":"title-middle-caret","raw":"Това е Друфа дума","targetTokenText":"Друфа","candidate":"друга","expectedButtonReplacement":"Друга","expectedWholeText":"Това е Друга дума","start":7,"end":12,"initialSelection":[9,9],"expectedSelection":[7,12],"mode":"preserve","tokenIndex":2},
  {"id":"caps-middle-caret","raw":"Това е ДРУФА дума","targetTokenText":"ДРУФА","candidate":"друга","expectedButtonReplacement":"ДРУГА","expectedWholeText":"Това е ДРУГА дума","start":7,"end":12,"initialSelection":[9,9],"expectedSelection":[7,12],"mode":"preserve","tokenIndex":2},
  {"id":"emoji-NFD-prefix-internal-mark-second-occurrence","raw":"😀 ѝ. Дру́фа дума\r\nДру́фа дом.","targetTokenText":"Дру́фа","occurrenceOrdinal":2,"candidate":"дру́га","expectedButtonReplacement":"Дру́га","expectedWholeText":"😀 ѝ. Дру́фа дума\r\nДру́га дом.","start":20,"end":26,"initialSelection":[24,24],"expectedSelection":[20,26],"mode":"preserve","tokenIndex":3},
  {"id":"emoji-NFD-prefix-caps-internal-mark-second-occurrence","raw":"😀 ѝ. ДРУ́ФА дума\r\nДРУ́ФА дом.","targetTokenText":"ДРУ́ФА","occurrenceOrdinal":2,"candidate":"дру́га","expectedButtonReplacement":"ДРУ́ГА","expectedWholeText":"😀 ѝ. ДРУ́ФА дума\r\nДРУ́ГА дом.","start":20,"end":26,"initialSelection":[31,31],"expectedSelection":[31,31],"mode":"preserve","tokenIndex":3}
];

const SPELLING_CASE_BAD_RANGES = [
  {"id":"low-surrogate-start","raw":"😀 Друфа","start":1,"end":2,"expected":"no replacement"},
  {"id":"high-surrogate-only-end","raw":"😀 Друфа","start":0,"end":1,"expected":"no replacement"},
  {"id":"start-on-combining-mark","raw":"Др̀уфа","start":2,"end":6,"expected":"no replacement"},
  {"id":"end-before-combining-mark","raw":"Друфа́","start":0,"end":5,"expected":"no replacement"},
  {"id":"fractional-range","raw":"Друфа","start":0.5,"end":5,"expected":"no replacement"},
  {"id":"negative-range","raw":"Друфа","start":-1,"end":5,"expected":"no replacement"},
  {"id":"out-of-bounds","raw":"Друфа","start":0,"end":6,"expected":"no replacement"},
  {"id":"zero-reversed-range","raw":"Друфа","start":3,"end":2,"expected":"no replacement"},
  {"id":"token-slice-mismatch","raw":"Друфа","start":0,"end":5,"tokenText":"Другx","expected":"no replacement"}
];


// Assemble original wire positions only. This helper does not classify or transform case.
// singleWord deliberately supplies one opaque complete Word span, including unsupported
// case patterns; it does not claim that the server tokenizer emits such a Word.
function lifecycleSpellingCaseReply(text, {start=0,end=text.length,tokenText=text,candidate}, {singleWord=false}={}) {
  const reply=lifecycleReply(text);
  let tokenIndex;
  if(singleWord){
    tokenIndex=0;
    reply.data.tokens=[{kind:'Word',text:tokenText,start,length:end-start,analysis:{isKnown:false}}];
    reply.data.readingInventory={occurrences:[{tokenIndex:0,readingSetIndex:0}],sets:[{canonicalReadings:[],supportReadings:null}]};
    reply.data.profile.words=[{tokenIndex:0,fullReadingCount:0,observedProjectedAmbiguity:false,actualDisplayTruncated:false,issues:[]}];
    reply.data.spelling.words=[{tokenIndex:0,search:'Completed',alternatives:[]}];
  }else{
    tokenIndex=reply.data.tokens.findIndex(token=>token.start===start&&token.length===end-start&&token.text===tokenText);
    assert.ok(tokenIndex>=0,'The explicit target occurrence must exist in the wire fixture.');
    reply.data.tokens[tokenIndex].analysis={isKnown:false};
    const occurrence=reply.data.readingInventory.occurrences.find(row=>row.tokenIndex===tokenIndex);
    reply.data.readingInventory.sets[occurrence.readingSetIndex].canonicalReadings=[];
    reply.data.profile.words.find(row=>row.tokenIndex===tokenIndex).fullReadingCount=0;
  }
  const token=reply.data.tokens[tokenIndex],word=reply.data.spelling.words.find(row=>row.tokenIndex===tokenIndex);
  Object.assign(word,{text:token.text,start:token.start,length:token.length,normalizedQuery:'opaque-case-query',search:'Completed',reason:null,
    alternatives:[{form:candidate,distance:1,readings:[{form:candidate,lemma:'opaque-lemma',partOfSpeech:'opaque-case-tag',score:0.375,
      legacyTypeId:null,legacyBaseId:null,legacyWordId:null,source:'opaque-ui-wire'}]}]});
  return {reply,tokenIndex};
}

function onlySpellingCaseButton(f) {
  const buttons=Array.from(f.spelling.querySelectorAll('button'));
  assert.equal(buttons.length,1,'Only the explicitly supplied target has an alternative.');
  return buttons[0];
}

function assertSpellingCaseLabel(button,expected) {
  assert.equal(button.textContent.split(' · ')[0],expected);
  assert.ok(button.textContent.includes(' · разстояние 1 · '),'Raw server distance is still visible.');
}

test('actual Live spelling presentation applies eighteen literal case policies without changing raw wire evidence', {concurrency:false}, async()=>{
  for(const row of SPELLING_CASE_LITERAL_ROWS)await withLiveLifecycle(async f=>{
    const {reply}=lifecycleSpellingCaseReply(row.source,{candidate:row.candidate},{singleWord:true}),before=structuredClone(reply);
    await f.first(reply);
    if (row.id === 'leading-mark-source-unsupported') {
      assert.equal(f.spelling.querySelectorAll('button').length,0,'A leading mark is an invalid Word start, so no action is offered.');
      assert.equal(f.editor.value,row.source);assert.equal(f.editor.setRangeTextCalls.length,0);assert.deepEqual(reply,before);return;
    }
    const button=onlySpellingCaseButton(f);assertSpellingCaseLabel(button,row.expected);
    assert.equal(f.editor.value,row.source,row.id);assert.equal(f.editor.setRangeTextCalls.length,0,'Showing a form never edits the source.');
    assert.deepEqual(reply,before,'Raw alternative form, distance, reading score and all source evidence stay immutable.');
    button.click();
    assert.equal(f.editor.value,row.expected,row.id);
    assert.deepEqual(f.editor.setRangeTextCalls,[{form:row.expected,start:0,end:row.source.length,mode:'preserve'}],row.id);
    assert.equal(f.editor.selectionStart,0);assert.equal(f.editor.selectionEnd,0);assert.strictEqual(f.doc.activeElement,f.editor);
    assert.deepEqual(reply,before,'Case presentation must not be written back into candidate/readings data.');
  },{text:row.source,caret:0});
});

test('actual Live spelling keeps four literal original Unicode occurrence ranges and native preserve selections', {concurrency:false}, async()=>{
  for(const row of SPELLING_CASE_INTEGRATION_ROWS)await withLiveLifecycle(async f=>{
    const {reply,tokenIndex}=lifecycleSpellingCaseReply(row.raw,{start:row.start,end:row.end,tokenText:row.targetTokenText,candidate:row.candidate});
    assert.equal(tokenIndex,row.tokenIndex);assert.equal(row.raw.slice(row.start,row.end),row.targetTokenText);
    const before=structuredClone(reply);await f.first(reply);
    const button=onlySpellingCaseButton(f);assertSpellingCaseLabel(button,row.expectedButtonReplacement);
    assert.equal(f.editor.value,row.raw);assert.equal(f.editor.setRangeTextCalls.length,0);
    button.click();
    assert.equal(f.editor.value,row.expectedWholeText);
    assert.deepEqual(f.editor.setRangeTextCalls,[{form:row.expectedButtonReplacement,start:row.start,end:row.end,mode:'preserve'}]);
    assert.deepEqual([f.editor.selectionStart,f.editor.selectionEnd],row.expectedSelection);
    assert.strictEqual(f.doc.activeElement,f.editor);assert.deepEqual(reply,before);
    f.forceClick(button);assert.equal(f.editor.setRangeTextCalls.length,1,'The captured old action cannot apply twice.');
    await f.clock.advance(2025);assert.equal(f.net.calls.length,2);
    assert.equal(posted(f.net.calls[1]).text,row.expectedWholeText,'The next actual request preserves case, marks, first occurrence and CRLF/suffix.');
  },{text:row.raw,caret:row.initialSelection[0]});
});

test('actual Live spelling rejects nine malformed original ranges and keeps forced prior callbacks inert', {concurrency:false}, async()=>{
  const previous='Предишна Друфа дума.';
  for(const row of SPELLING_CASE_BAD_RANGES)await withLiveLifecycle(async f=>{
    const good=lifecycleSpellingCaseReply(previous,{start:9,end:14,tokenText:'Друфа',candidate:'друга'});
    await f.first(good.reply);const old=onlySpellingCaseButton(f);
    f.type(row.raw,0);await f.clock.advance(2025);assert.equal(f.net.calls.length,2);
    const bad=lifecycleSpellingCaseReply(row.raw,{start:row.start,end:row.end,tokenText:row.tokenText??row.raw.slice(row.start,row.end),candidate:'друга'},{singleWord:true});
    await f.accept(1,bad.reply);
    assert.equal(f.editor.value,row.raw,row.id);assert.equal(f.editor.setRangeTextCalls.length,0);
    // Full-frame validation may reject first, or spelling may omit the invalid action.
    // Neither route permits the malformed span or retained old controls to splice text.
    f.forceClick(old);
    for(const button of f.spelling.querySelectorAll('button'))f.forceClick(button);
    assert.equal(f.editor.value,row.raw,row.id);assert.equal(f.editor.setRangeTextCalls.length,0,row.id);
  },{text:previous,caret:0});
});

test('actual Live case-presented spelling keeps selection revision IME and forced stale callback guards', {concurrency:false}, async()=>{
  const raw='Това е Друфа дума',changed='Това е Друфа дума сега';
  await withLiveLifecycle(async f=>{
    const first=lifecycleSpellingCaseReply(raw,{start:7,end:12,tokenText:'Друфа',candidate:'друга'});
    await f.first(first.reply);const old=onlySpellingCaseButton(f);assertSpellingCaseLabel(old,'Друга');
    f.editor.setSelectionRange(13,13);f.forceClick(old);assert.equal(f.editor.setRangeTextCalls.length,0);
    f.editor.setSelectionRange(9,9);f.editor.value='Това е ДрУфа дума';f.forceClick(old);
    assert.equal(f.editor.setRangeTextCalls.length,0,'A pre-event raw edit cannot pass the captured source guard.');
    f.editor.value=raw;f.editor.dispatch('compositionstart');f.forceClick(old);await f.clock.advance(5000);
    assert.equal(f.net.calls.length,1);assert.equal(f.editor.setRangeTextCalls.length,0);assert.equal(f.editor.value,raw);
    f.editor.dispatch('compositionend');f.type(changed,9);f.forceClick(old);assert.equal(f.editor.setRangeTextCalls.length,0);
    await f.clock.advance(2025);assert.equal(f.net.calls.length,2);
    const fresh=lifecycleSpellingCaseReply(changed,{start:7,end:12,tokenText:'Друфа',candidate:'друга'}),before=structuredClone(fresh.reply);
    await f.accept(1,fresh.reply);const button=onlySpellingCaseButton(f);assertSpellingCaseLabel(button,'Друга');
    f.forceClick(old);assert.equal(f.editor.setRangeTextCalls.length,0,'The same typo occurrence in a new response does not revive the old binding.');
    button.click();assert.equal(f.editor.value,'Това е Друга дума сега');
    assert.deepEqual(f.editor.setRangeTextCalls,[{form:'Друга',start:7,end:12,mode:'preserve'}]);
    assert.deepEqual([f.editor.selectionStart,f.editor.selectionEnd],[7,12]);assert.strictEqual(f.doc.activeElement,f.editor);
    f.forceClick(old);f.forceClick(button);assert.equal(f.editor.setRangeTextCalls.length,1);assert.deepEqual(fresh.reply,before);
  },{text:raw,caret:9});
});
