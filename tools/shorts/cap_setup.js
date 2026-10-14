(async () => {
  document.head.innerHTML = `<style>
@font-face{font-family:P;src:url(/fonts/ark-pixel-12px.woff2)}
html,body{margin:0;background:transparent!important}
.c{display:inline-block;font:48px P;color:#fff;padding:18px 30px;background:rgba(8,14,24,.82);border:4px solid #3b5f8a;box-shadow:6px 6px 0 #000;text-align:center;line-height:1.25}
.c b{color:#ffcf4a;font-weight:normal}
.end{width:1080px;height:1920px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:40px;font-family:P;color:#cfe3f5;background:transparent}
.logo{font-size:150px;color:#fff;text-shadow:8px 8px 0 #0a0f18;letter-spacing:10px}
.t{font-size:56px;text-align:center}.u{font-size:52px;color:#ffcf4a;background:rgba(8,14,24,.85);padding:16px 28px;border:4px solid #3b5f8a}
</style>`
  document.body.innerHTML = ''
  await document.fonts.load('48px P')
  return true
})()
