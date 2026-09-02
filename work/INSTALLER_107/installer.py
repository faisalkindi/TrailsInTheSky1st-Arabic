"""Trails in the Sky 1st Chapter — Arabic Localization installer (game v1.07).
Single self-contained GUI. Detects the Steam install, gates on game version/integrity, backs up the
originals, swaps in 5 modded pacs, and patches the 23 changed image.pac entries in place. Reversible.
"""
import os, sys, json, struct, hashlib, shutil, threading, traceback
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

APP="Trails in the Sky 1st — Arabic Localization"
def res(*p):
    base=getattr(sys,"_MEIPASS",os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base,"data",*p)
MANIFEST=json.load(open(res("manifest.json"),encoding="utf-8"))

# ---------- pac helpers ----------
def rc(b,o): return b[o:b.index(b'\x00',o)].decode('utf-8','replace')
def pac_index(path):
    with open(path,'rb') as f:
        head=f.read(64); cnt,hsz,_=struct.unpack_from('<3I',head,4); f.seek(0); b=f.read(hsz+400000)
    d={}
    for i in range(cnt):
        h,no,sz,loc=struct.unpack_from('<4Q',b,16+i*32); d[rc(b,no)]=(loc,sz)
    return d
def md5_at(path,loc,sz):
    with open(path,'rb') as f: f.seek(loc); return hashlib.md5(f.read(sz)).hexdigest()
def md5_file(path):
    h=hashlib.md5()
    with open(path,'rb') as f:
        for chunk in iter(lambda:f.read(1<<20),b''): h.update(chunk)
    return h.hexdigest()

# ---------- game detection ----------
def steam_libraries():
    libs=[]
    try:
        import winreg
        k=winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam")
        steam=winreg.QueryValueEx(k,"SteamPath")[0].replace('/','\\')
        libs.append(steam)
        vdf=os.path.join(steam,"steamapps","libraryfolders.vdf")
        if os.path.exists(vdf):
            import re
            for m in re.finditer(r'"path"\s*"([^"]+)"', open(vdf,encoding='utf-8',errors='ignore').read()):
                libs.append(m.group(1).replace('\\\\','\\'))
    except Exception:
        pass
    return libs
def find_game():
    for lib in steam_libraries():
        g=os.path.join(lib,"steamapps","common","Trails in the Sky 1st Chapter")
        if os.path.isdir(os.path.join(g,"pac","steam")):
            return g
    return ""

# ---------- core ----------
class Installer:
    def __init__(self, game, log, prog):
        self.game=game; self.log=log; self.prog=prog
        self.pacdir=os.path.join(game,"pac","steam")
        self.bak=os.path.join(game,"_arabic_mod_backup")
        self.imgpac=os.path.join(self.pacdir,"image.pac")

    def status(self):
        """vanilla | installed | partial | mismatch | missing

        'partial' means every entry is recognisable -- each is either the vanilla or the modded
        version -- but they disagree with each other. That happens after a Steam update lands on
        a modded install and rewrites most, but not all, of image.pac. It is safe to install over.
        """
        if not os.path.exists(self.imgpac): return "missing"
        idx=pac_index(self.imgpac)
        van=mod=0
        for e in MANIFEST["image_entries"]:
            if e["name"] not in idx: return "mismatch"
            loc,sz=idx[e["name"]]
            if sz!=e["size"]: return "mismatch"
            h=md5_at(self.imgpac,loc,sz)
            if h==e["md5_vanilla"]: van+=1
            elif h==e["md5_mod"]: mod+=1
            else: return "mismatch"
        n=len(MANIFEST["image_entries"])
        if mod==n: return "installed"
        if van==n: return "vanilla"
        return "partial"

    def _backup(self):
        """Back up the current originals.

        Only ever stores bytes we can prove are vanilla. A backup taken from an already-modded
        file would make 'Restore Vanilla' a no-op, so those are left alone and the existing
        backup (or the vanilla copy shipped with this installer) is used instead.
        """
        os.makedirs(os.path.join(self.bak,"pacs"),exist_ok=True)
        os.makedirs(os.path.join(self.bak,"image_vanilla"),exist_ok=True)
        stamp=os.path.join(self.bak,"game_version.txt")
        prev=open(stamp,encoding="utf-8").read().strip() if os.path.exists(stamp) else ""
        fresh = prev!=MANIFEST["game_version"]
        if fresh and prev:
            self.log("backup is from game v%s, refreshing for v%s"%(prev,MANIFEST["game_version"]))

        mod_md5={p["name"]:p["md5"] for p in MANIFEST["full_pacs"]}
        for p in MANIFEST["full_pacs"]:
            src=os.path.join(self.pacdir,p["name"]); dst=os.path.join(self.bak,"pacs",p["name"])
            if md5_file(src)==mod_md5[p["name"]]:
                self.log("skip backup of %s (already the modded file)"%p["name"]); continue
            if fresh or not os.path.exists(dst):
                self.log("backup %s"%p["name"]); shutil.copyfile(src,dst)

        idx=pac_index(self.imgpac)
        for e in MANIFEST["image_entries"]:
            dst=os.path.join(self.bak,"image_vanilla",e["file"])
            loc,sz=idx[e["name"]]
            with open(self.imgpac,'rb') as f: f.seek(loc); data=f.read(sz)
            if hashlib.md5(data).hexdigest()!=e["md5_vanilla"]:
                continue                       # not vanilla right now; never overwrite a good backup with it
            if fresh or not os.path.exists(dst):
                open(dst,'wb').write(data)
        open(stamp,'w',encoding="utf-8").write(MANIFEST["game_version"])
        self.log("backup complete -> %s"%self.bak)

    def _vanilla_bytes(self,e):
        """Vanilla bytes for one image entry: prefer the user's backup, fall back to the copy
        shipped inside this installer for entries whose backup is known to be unusable."""
        p=os.path.join(self.bak,"image_vanilla",e["file"])
        if os.path.exists(p):
            b=open(p,'rb').read()
            if hashlib.md5(b).hexdigest()==e["md5_vanilla"]: return b
        p=res("vanilla_entries",e["file"])
        if os.path.exists(p):
            b=open(p,'rb').read()
            if hashlib.md5(b).hexdigest()==e["md5_vanilla"]: return b
        return None

    def _patch_image(self, get_bytes):
        idx=pac_index(self.imgpac); n=len(MANIFEST["image_entries"]); done=0
        with open(self.imgpac,'r+b') as f:
            for i,e in enumerate(MANIFEST["image_entries"]):
                loc,sz=idx[e["name"]]; data=get_bytes(e)
                if data is None:
                    self.log("  no vanilla copy for %s — left as is"%os.path.basename(e["name"])); continue
                assert len(data)==sz, "size mismatch for %s"%e["name"]
                f.seek(loc); f.write(data); done+=1
                self.prog(60+int(35*(i+1)/n))
        self.log("patched %d/%d image.pac entries in place"%(done,n))

    def install(self):
        st=self.status(); self.log("game status: %s"%st)
        if st=="missing":
            raise RuntimeError("image.pac not found — is this the right game folder?")
        if st=="mismatch":
            raise RuntimeError("Game files don't match the expected vanilla v%s (different game version, "
                               "or modded by something else). Aborting to stay safe — run Steam's "
                               "'Verify integrity of game files' first."%MANIFEST["game_version"])
        if st=="installed":
            self.log("Arabic mod is already installed — nothing to do."); self.prog(100); return
        if st=="partial":
            self.log("found a part-installed state (a game update overwrote some files) — reinstalling")
        self.prog(5); self._backup(); self.prog(40)
        for p in MANIFEST["full_pacs"]:
            self.log("install %s"%p["name"]); shutil.copyfile(res("pacs",p["name"]),os.path.join(self.pacdir,p["name"]))
        self.prog(60)
        self._patch_image(lambda e: open(res("image_entries",e["file"]),'rb').read())
        self.prog(100); self.log("\nDONE — Arabic localization installed. Launch the game!")

    def restore(self):
        if not os.path.isdir(self.bak):
            raise RuntimeError("No backup found — nothing to restore (use Steam 'Verify integrity of game files').")
        self.prog(5)
        for p in MANIFEST["full_pacs"]:
            src=os.path.join(self.bak,"pacs",p["name"])
            if os.path.exists(src): self.log("restore %s"%p["name"]); shutil.copyfile(src,os.path.join(self.pacdir,p["name"]))
        self.prog(55)
        self._patch_image(self._vanilla_bytes)
        self.prog(100); self.log("\nDONE — vanilla restored.")

# ---------- GUI ----------
class App:
    def __init__(self,root):
        self.root=root; root.title(APP); root.geometry("640x460"); root.resizable(False,False)
        tk.Label(root,text=APP,font=("Segoe UI",13,"bold")).pack(pady=(12,2))
        tk.Label(root,text="Modern Standard Arabic — mod v%s, for game v%s"
                 %(MANIFEST.get("mod_version","1.1.0"),MANIFEST["game_version"]),fg="#555").pack()
        fr=tk.Frame(root); fr.pack(fill="x",padx=16,pady=10)
        tk.Label(fr,text="Game folder:").pack(anchor="w")
        row=tk.Frame(fr); row.pack(fill="x")
        self.path=tk.StringVar(value=find_game())
        tk.Entry(row,textvariable=self.path).pack(side="left",fill="x",expand=True)
        tk.Button(row,text="Browse…",command=self.browse).pack(side="left",padx=(6,0))
        bfr=tk.Frame(root); bfr.pack(pady=6)
        self.binstall=tk.Button(bfr,text="Install Arabic Mod",width=20,height=2,bg="#2d7",command=lambda:self.run("install"))
        self.binstall.pack(side="left",padx=6)
        self.brestore=tk.Button(bfr,text="Restore Vanilla",width=18,height=2,command=lambda:self.run("restore"))
        self.brestore.pack(side="left",padx=6)
        self.pb=ttk.Progressbar(root,length=600,mode="determinate"); self.pb.pack(pady=6)
        self.txt=tk.Text(root,height=13,width=78,font=("Consolas",9)); self.txt.pack(padx=16,pady=(2,12))
        self.log("Ready. Detected game: %s"%(self.path.get() or "(not found — Browse to select)"))
    def browse(self):
        d=filedialog.askdirectory(title="Select the 'Trails in the Sky 1st Chapter' folder")
        if d: self.path.set(d)
    # all tk updates marshalled to the main thread (worker runs in a background thread)
    def log(self,m): self.root.after(0,self._log,m)
    def _log(self,m): self.txt.insert("end",m+"\n"); self.txt.see("end")
    def prog(self,v): self.root.after(0,lambda:self.pb.configure(value=v))
    def _done(self,mode,err):
        self.binstall["state"]="normal"; self.brestore["state"]="normal"
        if err: messagebox.showerror(APP,err)
        else: messagebox.showinfo(APP,"Done — "+("Arabic mod installed!" if mode=="install" else "vanilla restored."))
    def run(self,mode):
        g=self.path.get().strip()
        if not os.path.isdir(os.path.join(g,"pac","steam")):
            messagebox.showerror(APP,"That folder doesn't contain pac\\steam. Pick the game's install folder."); return
        self.binstall["state"]="disabled"; self.brestore["state"]="disabled"; self.prog(0)
        def work():
            err=None
            try:
                inst=Installer(g,self.log,self.prog)
                (inst.install if mode=="install" else inst.restore)()
            except Exception as e:
                self.log("\nERROR: %s"%e); err=str(e); traceback.print_exc()
            self.root.after(0,self._done,mode,err)
        threading.Thread(target=work,daemon=True).start()

if __name__=="__main__":
    r=tk.Tk(); App(r); r.mainloop()
