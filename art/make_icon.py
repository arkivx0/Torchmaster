# Generates the Lanternly icon: a lantern glow on a Tokyo Night background. No fonts used.
from PIL import Image, ImageDraw, ImageFilter
BG=(26,27,38,255); GLOW=(224,175,104,255); FRAME=(122,162,247,255); S=1024

def icon(size=S, bg=True):
    im=Image.new("RGBA",(size,size),(0,0,0,0)); k=size/1024
    if bg: ImageDraw.Draw(im).rounded_rectangle([0,0,size-1,size-1], radius=int(size*0.22), fill=BG)
    halo=Image.new("RGBA",(size,size),(0,0,0,0)); ImageDraw.Draw(halo).ellipse([292*k,352*k,732*k,792*k], fill=(224,175,104,90))
    im.alpha_composite(halo.filter(ImageFilter.GaussianBlur(60*k)))
    d=ImageDraw.Draw(im)
    d.rounded_rectangle([392*k,392*k,632*k,752*k], radius=int(90*k), fill=GLOW)          # glass
    d.rounded_rectangle([352*k,330*k,672*k,400*k], radius=int(30*k), fill=FRAME)         # cap
    d.rounded_rectangle([352*k,744*k,672*k,800*k], radius=int(26*k), fill=FRAME)         # base
    d.arc([432*k,190*k,592*k,370*k], 180, 360, fill=FRAME, width=max(2,int(34*k)))      # handle
    d.rounded_rectangle([497*k,440*k,527*k,700*k], radius=int(15*k), fill=(255,236,200,255)) # flame
    return im
big=icon(); big.save("icon-512.png" if False else "icon-1024.png")
big.save("lanternly.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
for n,s in {"mdpi":48,"hdpi":72,"xhdpi":96,"xxhdpi":144,"xxxhdpi":192}.items():
    icon(1024).resize((s,s),Image.LANCZOS).save(f"ic_launcher_{n}.png")
