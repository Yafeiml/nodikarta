const refresh = document.querySelector("#refresh-login-captcha");
refresh.addEventListener("submit", async event => {
  event.preventDefault();
  const button = document.querySelector('[form="refresh-login-captcha"]');
  button.disabled = true;
  try {
    const response = await fetch(refresh.action, { method: "POST", body: new FormData(refresh) });
    const page = new DOMParser().parseFromString(await response.text(), "text/html");
    const challenge = page.querySelector("[data-login-challenge]");
    if (!response.ok || !challenge) throw new Error();
    document.querySelector("[data-login-challenge]").replaceWith(challenge);
    refresh.elements.challengeId.value = challenge.querySelector('[name="challengeId"]').value;
    document.querySelector("#captcha").focus();
  } catch {
    // A full navigation also recovers when the server completed the refresh but the response was lost.
    location.assign(location.pathname);
  }
});
