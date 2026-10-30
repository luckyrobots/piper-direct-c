// Browser-local camera profiles, shared by Windows and Linux dashboards.
const $ = id => document.getElementById(id);
export function setupCamera({key, videoId, deviceId, listId, startId, stopId, statusId, emptyId, preferred, width=1280, height=720}) {
  let stream, saved, busy=false, wanted=true;
  try { saved=JSON.parse(localStorage.getItem(key)||'null'); } catch {}
  if(saved?.off)wanted=false;
  const status=text=>$(statusId).textContent=text;
  const remember=()=>{ try { localStorage.setItem(key,JSON.stringify(saved)); } catch {} };
  function stop() {
    stream?.getTracks().forEach(track=>track.stop());stream=null;
    $(videoId).srcObject=null;$(videoId).hidden=true;$(stopId).disabled=true;
    if(emptyId) { $(emptyId).hidden=false;$(emptyId).textContent='Camera is off'; }
  }
  async function list(permission=false) {
    if (!navigator.mediaDevices) throw Error('Camera access is unavailable in this browser');
    if(permission) { const temporary=await navigator.mediaDevices.getUserMedia({video:true,audio:false});temporary.getTracks().forEach(track=>track.stop()); }
    const devices=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='videoinput');
    $(deviceId).replaceChildren(new Option('Off',''),...devices.map((d,i)=>new Option(d.label||`Camera ${i+1}`,d.deviceId)));
    const match=saved ? devices.find(d=>d.deviceId===saved.id)||devices.find(d=>d.label&&d.label===saved.label) : devices.find(d=>d.label.toLowerCase().includes(preferred.toLowerCase()));
    $(deviceId).value=match?.deviceId||'';
    if(match) { saved={id:match.deviceId,label:match.label};remember(); }
    return match;
  }
  async function start() {
    if(busy)return;busy=true;stop();
    try {
      const id=$(deviceId).value;
      if(!id)throw Error(saved?'Saved camera is unavailable; reconnect it or choose another camera':'Choose a camera');
      try { stream=await navigator.mediaDevices.getUserMedia({video:{deviceId:{exact:id},width:{ideal:width},height:{ideal:height},frameRate:{ideal:15}},audio:false}); }
      catch(e) {
        if(!['NotReadableError','OverconstrainedError'].includes(e.name))throw e;
        stream=await navigator.mediaDevices.getUserMedia({video:{deviceId:{exact:id}},audio:false});
      }
      saved={id,label:$(deviceId).selectedOptions[0]?.textContent||''};remember();
      $(videoId).srcObject=stream;await $(videoId).play();$(videoId).hidden=false;$(stopId).disabled=false;
      if(emptyId)$(emptyId).hidden=true;
      status(`LIVE · ${saved.label} · local preview`);
      stream.getVideoTracks()[0].onended=()=>{stop();status('Camera disconnected · reconnecting');};
    }catch(e){stop();status(`Camera: ${e.message}`);}finally{busy=false;}
  }
  $(listId).onclick=async()=>{try{await list(true);status('Select a camera. The selection is remembered.');}catch(e){status(e.message);}};
  $(deviceId).onchange=async()=>{wanted=!!$(deviceId).value;if(!wanted){saved={off:true};remember();stop();status('Camera off');return;} saved={id:$(deviceId).value,label:$(deviceId).selectedOptions[0]?.textContent||''};remember();await start();};
  $(startId).onclick=()=>{wanted=true;return start();};
  $(stopId).onclick=()=>{wanted=false;stop();status('Camera paused · saved selection will start on next load');};
  window.addEventListener('pagehide',()=>{wanted=false;stop();});
  const restore=async()=>{if(!wanted||stream||busy||document.hidden)return;try{await list();if($(deviceId).value)await start();}catch(e){status(e.message);}};
  document.addEventListener?.('visibilitychange',()=>{if(document.hidden)stop();else restore();});
  navigator.mediaDevices?.addEventListener?.('devicechange',restore);
  setInterval(restore,5000);restore();
  return {stop};
}
if(typeof document!=='undefined' && $('bench-video')) {
  setupCamera({key:'piper.camera.bench.v1',videoId:'bench-video',deviceId:'bench-device',listId:'bench-list',startId:'camera-start',stopId:'camera-stop',statusId:'camera-status',emptyId:'camera-empty',preferred:'Lumina'});
  $('camera-enlarge').onclick=()=>{const expanded=$('camera-panel').classList.toggle('expanded');$('camera-enlarge').textContent=expanded?'Shrink':'Enlarge';$('camera-enlarge').setAttribute('aria-expanded',String(expanded));};
}
if(typeof document!=='undefined' && $('depth-video')) {
  setupCamera({key:'piper.camera.depth.v1',videoId:'depth-video',deviceId:'depth-camera-device',listId:'depth-camera-list',startId:'depth-camera-start',stopId:'depth-camera-stop',statusId:'depth-camera-status',preferred:'RealSense',width:1280,height:720});
  for(const [button,panel] of [['depth-camera-enlarge','depth-camera-panel'],['gripper-camera-enlarge','gripper-camera-panel']]) {
    $(button).onclick=()=>{const expanded=$(panel).classList.toggle('enlarged');$(button).textContent=expanded?'Shrink':'Enlarge';$(button).setAttribute('aria-expanded',String(expanded));};
  }
}

if(typeof document!=='undefined' && document.querySelectorAll) {
  for(const toggle of document.querySelectorAll('.camera-settings-toggle')) {
    toggle.onclick=()=>{const panel=$(toggle.getAttribute('aria-controls'));const open=panel.hidden;panel.hidden=!open;toggle.setAttribute('aria-expanded',String(open));};
  }
}
