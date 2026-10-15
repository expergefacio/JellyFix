const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const http=require('node:http');
const path=require('node:path');
(async()=>{
 let checks=0;const check=(condition,label)=>{assert.ok(condition,label);checks++;console.log('PASS '+label);};
 const browser=await chromium.launch({headless:true,executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH||undefined,args:['--no-sandbox']});
 const page=await browser.newPage();
 const html=fs.readFileSync(path.join(__dirname,'../src/Configuration/configPage.html'),'utf8');
 await page.evaluate(()=>{
  window.__config={FirstSubtitleLanguage:'eng',SecondSubtitleLanguage:'nor',UseAnySubtitleFallback:true,MaximumTranscodeHeight:720,MaximumTranscodeMbps:1.2,BrowserTitleOverride:''};
  window.Dashboard={showLoadingMsg(){},hideLoadingMsg(){}};
  window.ApiClient={getUrl:x=>x,getJSON:async()=>[
   {Name:'English',DisplayName:'English',ThreeLetterISOLanguageNames:['eng']},
   {Name:'Norwegian',DisplayName:'Norwegian',ThreeLetterISOLanguageNames:['nor']},
   {Name:'French',DisplayName:'French',ThreeLetterISOLanguageNames:['fra','fre']},
   {Name:'German',DisplayName:'German',ThreeLetterISOLanguageNames:['deu','ger']}],
   getPluginConfiguration:async()=>JSON.parse(JSON.stringify(window.__config)),
   updatePluginConfiguration:async(id,c)=>{window.__config=c;window.__saved=c;return {};}};
 });
 await page.setContent(html);
 await page.evaluate(()=>document.querySelector('#JellyFixConfigPage').dispatchEvent(new Event('pageshow')));
 await page.waitForFunction(()=>document.querySelector('#JellyFixFirstLanguage').options.length>=4);
 check(await page.locator('#JellyFixFirstLanguage').inputValue()==='eng','First language loads saved/default value');
 check(await page.locator('#JellyFixSecondLanguage').inputValue()==='nor','Second language loads saved/default value');
 check((await page.locator('#JellyFixFallback option').allTextContents()).join('|')==='Any subtitle|No subtitles','Third priority has only any/none options');
 await page.selectOption('#JellyFixFirstLanguage','fra');await page.selectOption('#JellyFixSecondLanguage','deu');
 await page.selectOption('#JellyFixFallback','none');await page.selectOption('#JellyFixResolution','1080');
 await page.fill('#JellyFixBitrate','3,5');await page.fill('#JellyFixTitle','Private Media');await page.click('#JellyFixSave');
 await page.waitForFunction(()=>window.__saved);
 const saved=await page.evaluate(()=>window.__saved);
 check(saved.FirstSubtitleLanguage==='fra'&&saved.SecondSubtitleLanguage==='deu'&&!saved.UseAnySubtitleFallback,'Language dropdowns and no-subtitle fallback save');
 check(saved.MaximumTranscodeHeight===1080&&saved.MaximumTranscodeMbps===3.5,'Resolution and decimal-comma bitrate save correctly');
 check(saved.BrowserTitleOverride==='Private Media','Title override saves');
 await page.fill('#JellyFixBitrate','0.1');await page.click('#JellyFixSave');
 check((await page.locator('#JellyFixStatus').textContent()).includes('between 0.2 and 1000'),'Invalid bitrate shows a usable validation message');
 check((await page.evaluate(()=>window.__config.MaximumTranscodeMbps))===3.5,'Invalid form input does not overwrite saved configuration');
 await page.selectOption('#JellyFixResolution','0');await page.fill('#JellyFixBitrate','0');await page.fill('#JellyFixTitle','');await page.click('#JellyFixSave');
 await page.waitForFunction(()=>window.__saved.MaximumTranscodeMbps===0);
 check((await page.evaluate(()=>window.__saved.MaximumTranscodeHeight))===0,'No-limit settings can be saved');
 check((await page.evaluate(()=>window.__saved.BrowserTitleOverride))==='','Blank title disables the override');

 const scriptPath=[path.join(__dirname,'../research/title-test.js'),path.join(__dirname,'../../research/title-test.js')].find(p=>fs.existsSync(p));
 const script=fs.readFileSync(scriptPath,'utf8');
 const server=http.createServer((req,res)=>{res.setHeader('Content-Type','text/html; charset=utf-8');res.end('<html><head><title>Jellyfin</title></head><body>Media</body></html>');});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 await page.goto('http://127.0.0.1:'+server.address().port+'/web/index.html');
 await page.addScriptTag({content:script});
 check(await page.title()==='Private Media','Real browser applies fixed title');
 await page.evaluate(()=>{document.title='Secret Movie Name';history.pushState({},'','#/details?id=123');});
 check(await page.title()==='Private Media','Application title setters cannot expose media titles');
 await page.evaluate(()=>document.querySelector('title').textContent='Another Secret');
 await page.waitForFunction(()=>document.querySelector('title').textContent==='Private Media');
 check(await page.title()==='Private Media','Direct title-node mutation is repaired');
 await page.evaluate(()=>{const next=document.createElement('title');next.textContent='New secret';document.querySelector('title').replaceWith(next);});
 await page.waitForFunction(()=>document.querySelector('title').textContent==='Private Media');
 check(await page.title()==='Private Media','Replacement title elements are handled');
 const cdp=await page.context().newCDPSession(page);const navigation=await cdp.send('Page.getNavigationHistory');
 check(navigation.entries.filter(e=>e.url.includes('/web/index.html')).every(e=>e.title==='Private Media'),'New browser history entries carry the fixed title');
 check(page.url().includes('id=123'),'Title override leaves navigation URLs intact');
 await browser.close();await new Promise(r=>server.close(r));console.log('All '+checks+' browser checks passed.');
})().catch(e=>{console.error(e);process.exit(1);});
