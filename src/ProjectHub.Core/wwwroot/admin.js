(() => {
  document.addEventListener("change",event=>{
    const input=event.target.closest(".filter-options input[type=checkbox]");
    if (!input) return;
    const menu=input.closest(".filter-menu");
    const chosen=menu.querySelectorAll("input:checked");
    menu.querySelector("summary span").textContent=chosen.length === 0 ? menu.dataset.all
      : chosen.length === 1 ? chosen[0].closest("label").textContent.trim()
      : menu.dataset.multiple.replace("{0}",String(chosen.length));
  });
  document.addEventListener("click",event=>{
    document.querySelectorAll(".filter-menu[open],.language-picker[open]").forEach(menu=>{
      if(!menu.contains(event.target)) menu.open=false;
    });
  });
  document.addEventListener("keydown",event=>{
    const menu=event.target.closest(".filter-menu[open],.language-picker[open]");
    if(menu && event.key==="Escape") {
      menu.open=false;
      menu.querySelector("summary").focus();
    }
  });
  const t = (chinese, english) => document.documentElement.lang === "en" ? english : chinese;
  const labels = {pending:t("等待新 Edge 领取", "Awaiting redemption by the new Edge"),claimed:t("等待新 Edge 确认", "Awaiting confirmation from the new Edge"),confirmed:t("已完成", "Completed"),cancelled:t("已取消", "Cancelled"),expired:t("已过期", "Expired")};
  const origins = new WeakMap();
  const watchers = new WeakMap();
  const terminal = new Set(["confirmed","cancelled","expired"]);
  const clock = new Intl.DateTimeFormat(document.documentElement.dataset.dateLocale || document.documentElement.lang, {year:"numeric",month:"2-digit",day:"2-digit",hour:"2-digit",minute:"2-digit",timeZoneName:"short"});

  function times(root) {
    for (const time of root.querySelectorAll("time[datetime]")) {
      const d = new Date(time.dateTime);
      if (!Number.isNaN(d.getTime())) time.textContent = clock.format(d);
    }
  }
  function showDialog(dialog, trigger) {
    if (!dialog || dialog.open) return;
    origins.set(dialog, trigger);
    dialog.showModal();
    if (dialog.dataset.watch) watch(dialog);
  }
  async function progress(dialog, pairingId) {
    if (!dialog.open || dialog.dataset.watch !== pairingId) return;
    try {
      const response = await fetch("/devices?handler=Progress&pairingId=" + encodeURIComponent(pairingId),
        {headers:{Accept:"application/json"},cache:"no-store"});
      if (!response.ok || response.redirected) throw new Error();
      const status = await response.json();
      if (!dialog.open || dialog.dataset.watch !== pairingId) return;
      const line = dialog.querySelector("[data-progress]");
      if (line) {
        line.textContent = labels[status.status] ?? t("进度待确认", "Status pending");
        if (status.successor && status.status === "confirmed") {
          const link = document.createElement("a");
          link.href = "/devices/" + encodeURIComponent(status.successor);
          link.textContent = t(" 查看设备及实际授权", " View device and grants");
          line.append(link);
        }
      }
      const trigger = origins.get(dialog);
      const summary = trigger?.closest("tr")?.querySelector(".pending-state");
      if (summary) summary.textContent = labels[status.status] ?? t("进度待确认", "Status pending");
      if (terminal.has(status.status)) {
        clearInterval(watchers.get(dialog));
        dialog.querySelectorAll("form").forEach(form => form.hidden = true);
        const code = dialog.querySelector("#pairing-code");
        if (code) { code.value = ""; code.closest("label").hidden = true; dialog.querySelector("[data-copy-input]").hidden = true; }
        dialog.dataset.refresh = "true";
      }
    } catch {
      const line = dialog.querySelector("[data-progress]");
      if (line && dialog.open) line.textContent = t("暂时无法获取最新进度", "Latest status is temporarily unavailable");
    }
  }
  function watch(dialog) {
    const id = dialog.dataset.watch;
    clearInterval(watchers.get(dialog));
    progress(dialog,id);
    watchers.set(dialog,setInterval(() => progress(dialog,id),4000));
  }
  async function reloadDirectory(trigger) {
    const root = document.querySelector("#device-directory");
    if (!root) return;
    const selector = trigger?.dataset.open;
    try {
      const response = await fetch(location.href,{cache:"no-store"});
      const page = new DOMParser().parseFromString(await response.text(),"text/html");
      const updated = page.querySelector("#device-directory");
      if (!response.ok || !updated) throw new Error();
      if (document.querySelector("dialog[open]")) return;
      root.replaceWith(updated);
      times(updated);
      if (selector) (updated.querySelector('[data-open="'+CSS.escape(selector)+'"]') ?? updated.querySelector("button"))?.focus({preventScroll:true});
    } catch {
      // Fetch a fresh GET, including when the previous main document was a POST fallback.
      location.replace(location.pathname + location.search.split("handler=")[0].replace(/[?&]$/,""));
    }
  }
  async function refreshGrants(saved) {
    const panel=document.getElementById("grants");
    if (!panel) return;
    try {
      const response=await fetch(location.href,{cache:"no-store"});
      const page=new DOMParser().parseFromString(await response.text(),"text/html");
      const next=page.getElementById("grants");
      if (!response.ok || !next) throw new Error();
      const dialog=document.querySelector("#grant-device,#grant-project");
      if (dialog?.open) return;
      if (dialog) dialog.replaceWith(page.getElementById(dialog.id));
      panel.replaceWith(next);
      const tab=document.querySelector('.tabs a[aria-current="page"] span');
      const newTab=page.querySelector('.tabs a[aria-current="page"] span');
      if (tab && newTab) tab.textContent=newTab.textContent;
      const note=message(next.querySelector(".panel-head"),saved,"copy-status");
      note.tabIndex=-1;
      note.focus({preventScroll:true});
    } catch {
      message(panel.querySelector(".panel-head"),t("已保存。刷新页面可查看最新授权列表。", "Saved. Refresh to see the latest grants."),"copy-status");
    }
  }
  function message(parent, text, cls="form-error") {
    let line=parent.querySelector("."+cls);
    if (!line) {line=document.createElement("p"); line.className=cls; line.setAttribute("role","status"); parent.append(line);}
    line.textContent=text;
    return line;
  }
  async function copy(button) {
    const dialog=button.closest("dialog");
    const source=button.dataset.copy ? document.querySelector(button.dataset.copy) : dialog?.querySelector("#pairing-code");
    const value=button.dataset.copyText ?? source?.value ?? source?.textContent;
    if (!value) return;
    try {
      if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(value);
      else {
        const field=document.createElement("textarea"); field.value=value; field.className="copy-buffer";
        (dialog ?? document.body).append(field); field.select();
        const ok=document.execCommand("copy"); field.remove(); button.focus();
        if (!ok) throw new Error();
      }
      const original=button.textContent; button.textContent=t("已复制", "Copied");
      setTimeout(()=>button.textContent=original,1600);
    } catch {
      if (source?.select) {source.focus(); source.select();}
      else if (source) { const range=document.createRange(); range.selectNodeContents(source); const selection=window.getSelection(); selection.removeAllRanges(); selection.addRange(range); }
      else if (button.dataset.copyText) {
        const field=document.createElement("textarea"); field.readOnly=true; field.value=value; field.setAttribute("aria-label",t("诊断摘要", "Diagnostic summary"));
        button.after(field); field.focus(); field.select();
      }
      message(dialog ?? button.parentElement,t("请使用系统复制快捷键或长按复制。", "Use the system copy shortcut or press and hold to copy."),"copy-status");
    }
  }
  function issued(form, data) {
    const dialog=form.closest("dialog");
    dialog.querySelectorAll("form,.callout").forEach(el=>el.remove());
    dialog.querySelectorAll("p").forEach(p=>p.remove());
    const content=document.getElementById("issued-template").content.cloneNode(true);
    content.querySelector("input").value=data.code;
    content.querySelector("time").dateTime=data.expiresAt;
    dialog.append(content); times(dialog);
    dialog.dataset.issued=""; dialog.dataset.refresh="true";
    dialog.dataset.watch=data.pairingId;
    dialog.querySelector("[data-copy-input]").focus();
    watch(dialog);
  }

  document.addEventListener("click", event=>{
    const button=event.target.closest("button");
    if (!button) return;
    if (button.dataset.open) showDialog(document.getElementById(button.dataset.open),button);
    else if (button.hasAttribute("data-close")) button.closest("dialog")?.close();
    else if (button.hasAttribute("data-copy") || button.hasAttribute("data-copy-input") || button.hasAttribute("data-copy-text")) copy(button);
    else if (button.dataset.disclose) {
      const detail=document.getElementById(button.dataset.disclose);
      detail.hidden=!detail.hidden; button.setAttribute("aria-expanded",String(!detail.hidden));
      button.textContent=detail.hidden ? t("查看", "View") : t("收起", "Collapse");
    }
  });
  document.addEventListener("input",event=>{
    if (!event.target.matches("[data-filter]")) return;
    const value=event.target.value.trim().toLocaleLowerCase();
    event.target.closest("fieldset").querySelectorAll("[data-option]").forEach(el=>el.hidden=!el.firstChild.textContent.toLocaleLowerCase().includes(value));
  });
  document.addEventListener("close",event=>{
    const dialog=event.target;
    clearInterval(watchers.get(dialog));
    if (dialog.querySelector("[data-busy]")) dialog.dataset.abandoned="true";
    const field=dialog.querySelector("#pairing-code");
    if(field){field.value="";field.removeAttribute("value");field.closest("label")?.remove();}
    const trigger=origins.get(dialog);
    trigger?.focus({preventScroll:true});
    if (dialog.dataset.grantSaved) refreshGrants(t("项目权限已更新。", "Project permissions updated."));
    if(dialog.dataset.refresh || dialog.hasAttribute("data-issued")) reloadDirectory(trigger);
  },true);
  window.addEventListener("pagehide",()=>{
    document.querySelectorAll("#pairing-code").forEach(input=>{input.value=""; input.removeAttribute("value");});
  });
  document.addEventListener("submit",async event=>{
    const form=event.target;
    const grant=form.matches("[data-grant]");
    const cancel=form.matches("[data-cancel]");
    if(!grant && !cancel && !form.matches("[data-issue]")) return;
    event.preventDefault();
    const button=event.submitter ?? form.querySelector('button[type="submit"],button:last-child');
    if (form.dataset.busy) return;
    form.dataset.busy="true";
    const host=form.closest("dialog");
    if(host) delete host.dataset.abandoned;
    if(button) button.disabled=true;
    const data=new FormData(form);
    try {
      const response=await fetch(form.action,{method:"POST",headers:{Accept:"application/json"},body:data,cache:"no-store"});
      if(response.redirected) {location.assign(response.url);return;}
      const result=await response.json();
      if(cancel) {
        if(!response.ok) message(form,result.message??t("请刷新并核对当前接替进度。", "Refresh to check the current replacement status."));
        else {
          host.dataset.refresh="true";
          if(host.open) await progress(host,host.dataset.watch);
          else reloadDirectory(origins.get(host));
        }
      } else if(grant) {
        if(result.version!=null) {
          const selected=form.elements.accessMode.value;
          form.elements.expectedBindingId.value=result.bindingId ?? "";
          form.elements.expectedVersion.value=result.version;
          if(response.ok) form.elements.accessMode.value=result.mode;
          const line=form.querySelector(".row-result");
          line.textContent=result.message + (response.ok ? "" : t(" 当前权限：", " Current permission: ")+({readWrite:t("读写", "Read and write"),readOnly:t("只读", "Read only"),none:t("无权限", "No access")}[result.mode])+t("；所选值已保留。", ". Your selection has been kept."));
          line.classList.toggle("failed",!response.ok);
          if(response.ok && form.closest("dialog")) form.closest("dialog").dataset.grantSaved="true";
          else if(response.ok && result.mode==="none") refreshGrants(t("已撤销该项目授权。", "Project access revoked."));
          if(!response.ok) form.elements.accessMode.value=selected;
        } else message(form,result.message??t("保存失败，请刷新后重试。", "Save failed. Refresh and try again."),"row-result");
      } else if(response.ok) {
        const dialog=form.closest("dialog");
        if(dialog.open && !dialog.dataset.abandoned) issued(form,result);
        else {
          dialog.dataset.refresh="true";
          if(dialog.open) dialog.close();
          else reloadDirectory(origins.get(dialog));
        }
      }
      else {message(form,result.message??t("签发失败，请刷新后重试。", "Could not issue a code. Refresh and try again.")); if(response.status===409) form.closest("dialog").dataset.refresh="true";}
    } catch {
      message(form,t("暂时无法确认操作结果，请刷新并核对当前状态。", "The result could not be confirmed. Refresh to check the current status."),grant?"row-result":"form-error");
      if(host) host.dataset.refresh="true";
    }
    finally {delete form.dataset.busy; if(button) button.disabled=false;}
  });

  // Group consecutive equivalent reads within the visible page; each receipt remains individually accessible.
  function foldReads() {
    const rows=[...document.querySelectorAll("tr[data-activity-id]")];
    for(let i=0;i<rows.length;) {
      const head=rows[i++]; const key=head.dataset.group; if(!key) continue;
      const repeated=[];
      while(i<rows.length && rows[i].dataset.group===key &&
          Number(head.dataset.timestamp)-Number(rows[i].dataset.timestamp)<300000) repeated.push(rows[i++]);
      if(!repeated.length) continue;
      const button=document.createElement("button"); button.type="button"; button.className="text-button";
      button.textContent=t("共 "+(repeated.length+1)+" 次", (repeated.length+1)+" events"); button.setAttribute("aria-expanded","false");
      head.cells[3].append(button);
      repeated.forEach(row=>row.hidden=true);
      button.addEventListener("click",()=>{
        const expand=button.getAttribute("aria-expanded")==="false";
        repeated.forEach(row=>{row.hidden=!expand; if(!expand) {
          row.nextElementSibling.hidden=true;
          const control=row.querySelector("[data-disclose]");control.textContent=t("查看", "View");control.setAttribute("aria-expanded","false");
        }});
        button.setAttribute("aria-expanded",String(expand));
      });
    }
  }
  times(document); foldReads();
  document.querySelectorAll('dialog[data-auto-open="true"]').forEach(d=>{if(d.open)d.removeAttribute("open"); showDialog(d,null);});
})();
