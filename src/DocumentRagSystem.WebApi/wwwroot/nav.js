/**
 * ebm-papst Portal Shared Navigation & Account Management Component
 * Provides unified header, authentication status check, and self-service password change modal.
 */

// Detect active release version from script URL parameter (injected during package-release.ps1)
(function() {
    let ver = 'vdev';
    try {
        const scriptEl = document.currentScript || document.querySelector('script[src*="nav.js"]');
        if (scriptEl && scriptEl.src) {
            const url = new URL(scriptEl.src, window.location.origin);
            const vParam = url.searchParams.get('v');
            if (vParam) ver = `v${vParam}`;
        }
    } catch {}
    window.PORTAL_VERSION = ver;
})();

class PortalHeader extends HTMLElement {
    connectedCallback() {
        const active = this.getAttribute('active') || 'chat';
        const title = this.getAttribute('title') || 'Søgning i produkt dokumentation';
        const icon = this.getAttribute('icon') || 'fa-robot';
        const version = window.PORTAL_VERSION || 'vdev';

        const isChat = active === 'chat';
        const isDocs = active === 'documents';
        const isCalc = active === 'databeregning';
        const isUsers = active === 'users';

        const activeClass = 'bg-brand-600 text-white px-3.5 py-2 rounded-lg text-sm font-semibold shadow-sm flex items-center space-x-2 border border-brand-500';
        const inactiveClass = 'bg-brand-800/80 hover:bg-brand-700 text-slate-100 hover:text-white px-3.5 py-2 rounded-lg text-sm font-medium transition shadow-sm flex items-center space-x-2 border border-brand-700/40';

        this.innerHTML = `
        <header class="bg-brand-900 text-white shadow-md flex-shrink-0 z-30 sticky top-0">
            <div class="container mx-auto px-6 py-4 flex justify-between items-center">
                <div class="flex items-center space-x-3">
                    <div class="bg-brand-500 p-2 rounded-lg text-white shadow-sm flex items-center justify-center">
                        <i class="fa-solid ${icon} text-xl"></i>
                    </div>
                    <div class="flex items-center space-x-2.5">
                        <h1 class="text-xl font-bold tracking-tight">${title}</h1>
                        <span class="text-[10px] font-mono font-normal px-1.5 py-0.5 rounded bg-brand-800/90 text-brand-300 border border-brand-700/60 select-all cursor-default" title="Aktiv commit revision: ${version}">${version}</span>
                    </div>
                </div>
                <nav class="flex items-center space-x-2.5">
                    ${isChat 
                        ? `<span class="${activeClass}"><i class="fa-solid fa-comments text-xs"></i><span>Chat Assistent</span></span>`
                        : `<a href="index.html" class="${inactiveClass}"><i class="fa-solid fa-comments text-xs"></i><span>Chat Assistent</span></a>`
                    }
                    ${isDocs
                        ? `<span class="${activeClass}"><i class="fa-solid fa-file text-xs"></i><span>Dokumenter</span></span>`
                        : `<a href="documents.html" class="${inactiveClass}"><i class="fa-solid fa-file text-xs"></i><span>Dokumenter</span></a>`
                    }
                    ${isCalc
                        ? `<span class="${activeClass}"><i class="fa-solid fa-calculator text-xs"></i><span>Databeregning</span></span>`
                        : `<a href="databeregning.html" class="${inactiveClass}"><i class="fa-solid fa-calculator text-xs"></i><span>Databeregning</span></a>`
                    }
                    ${isUsers
                        ? `<span id="nav-users" class="${activeClass}"><i class="fa-solid fa-users text-xs"></i><span>Brugere</span></span>`
                        : `<a id="nav-users" href="users.html" class="${inactiveClass} hidden"><i class="fa-solid fa-users text-xs"></i><span>Brugere</span></a>`
                    }
                    <button onclick="openChangePasswordModal()" class="bg-brand-800/80 hover:bg-brand-700 text-slate-100 hover:text-white px-3.5 py-2 rounded-lg text-sm font-medium transition shadow-sm flex items-center space-x-1.5 border border-brand-700/40" title="Skift adgangskode">
                        <i class="fa-solid fa-key text-xs"></i>
                        <span class="hidden sm:inline">Skift kode</span>
                    </button>
                    <button onclick="handleLogout()" class="bg-brand-800/80 hover:bg-rose-900/60 text-slate-200 hover:text-rose-100 px-3.5 py-2 rounded-lg text-sm font-medium transition shadow-sm flex items-center space-x-1.5 ml-1 border border-brand-700/40" title="Log ud">
                        <i class="fa-solid fa-arrow-right-from-bracket text-xs"></i>
                        <span>Log ud</span>
                    </button>
                </nav>
            </div>
        </header>
        `;

        PortalAuth.ensureChangePasswordModal();
        PortalAuth.checkSession();

        // Update footer copyright line with version badge if present
        setTimeout(() => {
            const footerPara = document.querySelector('footer p');
            if (footerPara && !footerPara.innerHTML.includes(version)) {
                footerPara.innerHTML += ` &bull; <span class="font-mono text-xs opacity-75" title="Aktiv commit revision">${version}</span>`;
            }
        }, 50);
    }
}
customElements.define('portal-header', PortalHeader);

const PortalAuth = {
    async checkSession() {
        try {
            const res = await fetch('/api/auth/me');
            if (res.ok) {
                const data = await res.json();
                if (data && data.isAdmin) {
                    const navUsers = document.getElementById('nav-users');
                    if (navUsers) navUsers.classList.remove('hidden');
                }
            } else if (res.status === 401 && !window.location.pathname.endsWith('/login.html')) {
                window.location.href = '/login.html';
            }
        } catch {}
    },

    ensureChangePasswordModal() {
        if (document.getElementById('change-password-modal')) return;

        const modalHtml = `
        <div id="change-password-modal" class="fixed inset-0 z-50 hidden bg-slate-900/60 backdrop-blur-xs flex items-center justify-center p-4">
            <div class="bg-white rounded-2xl shadow-xl w-full max-w-md overflow-hidden border border-slate-200">
                <div class="flex items-center justify-between px-6 py-4 bg-slate-50 border-b border-slate-200">
                    <div class="flex items-center space-x-2.5">
                        <div class="w-8 h-8 rounded-lg bg-brand-100 text-brand-700 flex items-center justify-center">
                            <i class="fa-solid fa-key text-sm"></i>
                        </div>
                        <h3 class="font-bold text-slate-900 text-base">Skift adgangskode</h3>
                    </div>
                    <button onclick="closeChangePasswordModal()" class="text-slate-400 hover:text-slate-600 p-1.5 rounded-lg hover:bg-slate-200/60 transition">
                        <i class="fa-solid fa-xmark text-sm"></i>
                    </button>
                </div>
                <form id="change-password-form" onsubmit="handleChangePassword(event)" class="p-6 space-y-4 text-xs">
                    <div id="change-pw-error" class="hidden p-3 bg-red-50 text-red-700 border border-red-200 rounded-lg"></div>
                    <div id="change-pw-success" class="hidden p-3 bg-emerald-50 text-emerald-700 border border-emerald-200 rounded-lg font-medium"></div>

                    <div>
                        <label for="change-pw-current" class="block font-semibold text-slate-700 mb-1">Nuværende adgangskode <span class="text-red-500">*</span></label>
                        <input type="password" id="change-pw-current" required autocomplete="current-password"
                            class="w-full px-3.5 py-2 border border-slate-200 rounded-lg text-sm text-slate-900 focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-brand-500 bg-slate-50/50" />
                    </div>
                    <div>
                        <label for="change-pw-new" class="block font-semibold text-slate-700 mb-1">Ny adgangskode (min. 6 tegn) <span class="text-red-500">*</span></label>
                        <input type="password" id="change-pw-new" required minlength="6" autocomplete="new-password"
                            oninput="updatePasswordStrength(this.value, 'change-pw-strength-bar', 'change-pw-strength-text')"
                            class="w-full px-3.5 py-2 border border-slate-200 rounded-lg text-sm text-slate-900 focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-brand-500 bg-slate-50/50" />
                        <div class="mt-1.5 space-y-1">
                            <div class="h-1.5 w-full bg-slate-100 rounded-full overflow-hidden">
                                <div id="change-pw-strength-bar" class="h-full w-0 transition-all duration-300 rounded-full"></div>
                            </div>
                            <div class="flex justify-between items-center text-[10px] text-slate-400 font-medium">
                                <span id="change-pw-strength-text">Indtast adgangskode</span>
                                <span>Anbefalet: 8+ tegn, tal & symboler</span>
                            </div>
                        </div>
                    </div>
                    <div>
                        <label for="change-pw-confirm" class="block font-semibold text-slate-700 mb-1">Bekræft ny adgangskode <span class="text-red-500">*</span></label>
                        <input type="password" id="change-pw-confirm" required minlength="6" autocomplete="new-password"
                            class="w-full px-3.5 py-2 border border-slate-200 rounded-lg text-sm text-slate-900 focus:outline-none focus:ring-2 focus:ring-brand-500 focus:border-brand-500 bg-slate-50/50" />
                    </div>
                    <div class="flex justify-end space-x-2 pt-2">
                        <button type="button" onclick="closeChangePasswordModal()" class="px-4 py-2 bg-slate-200 hover:bg-slate-300 text-slate-700 font-medium rounded-lg transition text-xs">Annuller</button>
                        <button type="submit" id="change-pw-submit-btn" class="px-4 py-2 bg-brand-600 hover:bg-brand-500 text-white font-medium rounded-lg transition text-xs flex items-center space-x-1.5 shadow-sm">
                            <i class="fa-solid fa-check text-xs"></i>
                            <span>Gem adgangskode</span>
                        </button>
                    </div>
                </form>
            </div>
        </div>
        `;
        document.body.insertAdjacentHTML('beforeend', modalHtml);
    }
};

// Global Password Strength Calculation
function updatePasswordStrength(pw, barId, textId) {
    const bar = document.getElementById(barId);
    const text = document.getElementById(textId);
    if (!bar || !text) return;

    if (!pw) {
        bar.className = 'h-full w-0 transition-all duration-300 rounded-full';
        text.textContent = 'Indtast adgangskode';
        text.className = 'text-[10px] text-slate-400 font-medium';
        return;
    }

    let score = 0;
    if (pw.length >= 6) score++;
    if (pw.length >= 8) score++;
    if (/[0-9]/.test(pw)) score++;
    if (/[a-z]/.test(pw) && /[A-Z]/.test(pw)) score++;
    if (/[^A-Za-z0-9]/.test(pw)) score++;

    if (pw.length < 6) {
        bar.className = 'h-full w-1/4 bg-rose-500 transition-all duration-300 rounded-full';
        text.textContent = 'For kort (min. 6 tegn)';
        text.className = 'text-[10px] text-rose-600 font-medium';
    } else if (score <= 2) {
        bar.className = 'h-full w-1/3 bg-amber-500 transition-all duration-300 rounded-full';
        text.textContent = 'Styrke: Svag';
        text.className = 'text-[10px] text-amber-600 font-medium';
    } else if (score <= 4) {
        bar.className = 'h-full w-2/3 bg-blue-500 transition-all duration-300 rounded-full';
        text.textContent = 'Styrke: Middel';
        text.className = 'text-[10px] text-blue-600 font-medium';
    } else {
        bar.className = 'h-full w-full bg-emerald-500 transition-all duration-300 rounded-full';
        text.textContent = 'Styrke: Stærk';
        text.className = 'text-[10px] text-emerald-600 font-medium';
    }
}

// Global Change Password Modal Handlers
function openChangePasswordModal() {
    PortalAuth.ensureChangePasswordModal();
    const modal = document.getElementById('change-password-modal');
    const form = document.getElementById('change-password-form');
    const errEl = document.getElementById('change-pw-error');
    const succEl = document.getElementById('change-pw-success');
    if (form) form.reset();
    if (errEl) { errEl.classList.add('hidden'); errEl.textContent = ''; }
    if (succEl) { succEl.classList.add('hidden'); succEl.textContent = ''; }
    updatePasswordStrength('', 'change-pw-strength-bar', 'change-pw-strength-text');
    if (modal) {
        modal.classList.remove('hidden');
        const curInput = document.getElementById('change-pw-current');
        if (curInput) setTimeout(() => curInput.focus(), 50);
    }
}

function closeChangePasswordModal() {
    const modal = document.getElementById('change-password-modal');
    if (modal) modal.classList.add('hidden');
}

async function handleChangePassword(e) {
    e.preventDefault();
    const curInput = document.getElementById('change-pw-current');
    const newInput = document.getElementById('change-pw-new');
    const confInput = document.getElementById('change-pw-confirm');
    const errEl = document.getElementById('change-pw-error');
    const succEl = document.getElementById('change-pw-success');
    const submitBtn = document.getElementById('change-pw-submit-btn');

    if (errEl) errEl.classList.add('hidden');
    if (succEl) succEl.classList.add('hidden');

    const currentPassword = curInput ? curInput.value : '';
    const newPassword = newInput ? newInput.value : '';
    const confirmPassword = confInput ? confInput.value : '';

    if (newPassword !== confirmPassword) {
        if (errEl) {
            errEl.textContent = 'Den nye adgangskode og bekræftelsen matcher ikke.';
            errEl.classList.remove('hidden');
        }
        if (confInput) confInput.focus();
        return;
    }

    if (newPassword.length < 6) {
        if (errEl) {
            errEl.textContent = 'Ny adgangskode skal være på mindst 6 tegn.';
            errEl.classList.remove('hidden');
        }
        if (newInput) newInput.focus();
        return;
    }

    if (submitBtn) {
        submitBtn.disabled = true;
        submitBtn.innerHTML = '<i class="fa-solid fa-spinner animate-spin text-xs"></i><span>Gemmer...</span>';
    }

    try {
        const res = await fetch('/api/auth/change-password', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ currentPassword, newPassword })
        });

        const data = await res.json();
        if (res.ok) {
            if (succEl) {
                succEl.textContent = data.message || 'Adgangskoden er opdateret.';
                succEl.classList.remove('hidden');
            }
            if (curInput) curInput.value = '';
            if (newInput) newInput.value = '';
            if (confInput) confInput.value = '';
            setTimeout(() => closeChangePasswordModal(), 1800);
        } else {
            if (errEl) {
                errEl.textContent = data.message || 'Fejl under ændring af adgangskode.';
                errEl.classList.remove('hidden');
            }
        }
    } catch (err) {
        if (errEl) {
            errEl.textContent = 'Netværksfejl. Prøv venligst igen.';
            errEl.classList.remove('hidden');
        }
    } finally {
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.innerHTML = '<i class="fa-solid fa-check text-xs"></i><span>Gem adgangskode</span>';
        }
    }
}

async function handleLogout() {
    try {
        await fetch('/api/auth/logout', { method: 'POST' });
    } catch {}
    if (typeof conversations !== 'undefined') {
        currentConversationId = null;
        currentUserEmail = null;
    }
    window.location.href = '/login.html';
}

function checkAdminNav() {
    PortalAuth.checkSession();
}

// Cryptographically secure password generation
function generateSecurePassword(length = 14) {
    const uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // exclude easily confused I, O
    const lowercase = "abcdefghijkmnopqrstuvwxyz"; // exclude easily confused l
    const numbers = "23456789";                   // exclude easily confused 0, 1
    const symbols = "!@#$%^&*()-_=+[]{}";
    const allChars = uppercase + lowercase + numbers + symbols;

    const randomBytes = new Uint8Array(length);
    window.crypto.getRandomValues(randomBytes);

    let passwordChars = [
        uppercase[randomBytes[0] % uppercase.length],
        lowercase[randomBytes[1] % lowercase.length],
        numbers[randomBytes[2] % numbers.length],
        symbols[randomBytes[3] % symbols.length]
    ];

    for (let i = 4; i < length; i++) {
        passwordChars.push(allChars[randomBytes[i] % allChars.length]);
    }

    // Shuffle characters using Fisher-Yates with crypto random
    for (let i = passwordChars.length - 1; i > 0; i--) {
        const j = randomBytes[i] % (i + 1);
        [passwordChars[i], passwordChars[j]] = [passwordChars[j], passwordChars[i]];
    }

    return passwordChars.join('');
}

function generateAndSetPassword(inputId, barId, textId, confirmInputId) {
    const input = document.getElementById(inputId);
    if (!input) return;

    const pw = generateSecurePassword(14);
    input.value = pw;
    input.type = 'text'; // Show the generated password so admin can view/copy it

    // Update eye toggle icon if present
    const toggleBtn = input.parentElement ? input.parentElement.querySelector('button[onclick*="togglePasswordVisibility"]') : null;
    if (toggleBtn) {
        const icon = toggleBtn.querySelector('i');
        if (icon) icon.className = 'fa-solid fa-eye text-xs text-brand-600';
    }

    if (confirmInputId) {
        const confirmInput = document.getElementById(confirmInputId);
        if (confirmInput) {
            confirmInput.value = pw;
            confirmInput.type = 'text';
        }
    }

    if (typeof updatePasswordStrength === 'function' && barId && textId) {
        updatePasswordStrength(pw, barId, textId);
    }
}

function togglePasswordVisibility(inputId, btn) {
    const input = document.getElementById(inputId);
    if (!input) return;
    const isPw = input.type === 'password';
    input.type = isPw ? 'text' : 'password';
    if (btn) {
        const icon = btn.querySelector('i');
        if (icon) {
            icon.className = isPw ? 'fa-solid fa-eye text-xs text-brand-600' : 'fa-solid fa-eye-slash text-xs text-slate-400';
        }
    }
}

// Global keyboard accessibility: Escape closes open modal dialogs
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' || e.key === 'Esc') {
        const changePwModal = document.getElementById('change-password-modal');
        if (changePwModal && !changePwModal.classList.contains('hidden')) {
            closeChangePasswordModal();
        }
    }
});
