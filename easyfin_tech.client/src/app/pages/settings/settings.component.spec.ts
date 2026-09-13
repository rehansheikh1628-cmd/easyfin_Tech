import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { of, Observable } from 'rxjs';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SettingsComponent } from './settings.component';
import { AuthService, UserProfileDto } from '../../services/auth.service';

describe('SettingsComponent (Phase 11 UI Redesign)', () => {
  let component: SettingsComponent;
  let fixture: ComponentFixture<SettingsComponent>;
  let mockAuthService: {
    currentUser: UserProfileDto | null;
    currentUser$: Observable<UserProfileDto | null>;
    checkAuth: ReturnType<typeof vi.fn>;
    logout: ReturnType<typeof vi.fn>;
  };

  const sampleUser: UserProfileDto = {
    id: 'a54ec1f6-4b88-45a0-8b54-2f7110f6c754',
    userId: 'a54ec1f6-4b88-45a0-8b54-2f7110f6c754',
    fullName: 'Rahul Sharma',
    email: 'ca.sharma@easyfin.local',
    workspaceName: 'Sharma & Associates CA'
  };

  beforeEach(async () => {
    // Clear localStorage before each test
    try {
      localStorage.removeItem('easyfin_sidebar_collapsed');
    } catch {
      // Ignore
    }

    mockAuthService = {
      currentUser: sampleUser,
      currentUser$: of(sampleUser),
      checkAuth: vi.fn().mockReturnValue(of(sampleUser)),
      logout: vi.fn().mockReturnValue(of({ success: true }))
    };

    await TestBed.configureTestingModule({
      declarations: [SettingsComponent],
      imports: [CommonModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthService, useValue: mockAuthService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(SettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create component with default initial state', () => {
    expect(component).toBeTruthy();
    expect(component.activeTab).toBe('profile');
    expect(component.userIdCopied).toBe(false);
    expect(component.isSigningOut).toBe(false);
    expect(component.isCompactSidebar).toBe(false);
  });

  it('2. should bind current user profile details into the profile pane', () => {
    expect(component.currentUser).toEqual(sampleUser);
    expect(component.userInitials).toBe('RS');
    expect(component.effectiveUserId).toBe('a54ec1f6-4b88-45a0-8b54-2f7110f6c754');

    const compiled = fixture.nativeElement as HTMLElement;
    const firmInput = compiled.querySelector('#settingsFirmName') as HTMLInputElement;
    const nameInput = compiled.querySelector('#settingsFullName') as HTMLInputElement;
    const emailInput = compiled.querySelector('#settingsEmail') as HTMLInputElement;
    const userIdInput = compiled.querySelector('#settingsUserId') as HTMLInputElement;

    expect(firmInput?.value).toBe('Sharma & Associates CA');
    expect(nameInput?.value).toBe('Rahul Sharma');
    expect(emailInput?.value).toBe('ca.sharma@easyfin.local');
    expect(userIdInput?.value).toBe('a54ec1f6-4b88-45a0-8b54-2f7110f6c754');
  });

  it('3. should switch tabs via setTab() and update DOM', () => {
    // Switch to Security tab
    component.setTab('security');
    fixture.detectChanges();

    expect(component.activeTab).toBe('security');
    let compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('#securityPane')).toBeTruthy();
    expect(compiled.querySelector('#profilePane')).toBeNull();
    expect(compiled.querySelector('#preferencesPane')).toBeNull();

    // Switch to Preferences tab
    component.setTab('preferences');
    fixture.detectChanges();

    expect(component.activeTab).toBe('preferences');
    compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('#preferencesPane')).toBeTruthy();
    expect(compiled.querySelector('#securityPane')).toBeNull();

    // Switch back to Profile tab
    component.setTab('profile');
    fixture.detectChanges();

    expect(component.activeTab).toBe('profile');
    compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('#profilePane')).toBeTruthy();
  });

  it('4. should switch tabs by clicking navigation buttons in DOM', () => {
    const secTabBtn = fixture.debugElement.query(By.css('#tabBtnSecurity')).nativeElement as HTMLElement;
    secTabBtn.click();
    fixture.detectChanges();

    expect(component.activeTab).toBe('security');
    expect(fixture.nativeElement.querySelector('#securityPane')).toBeTruthy();

    const prefTabBtn = fixture.debugElement.query(By.css('#tabBtnPreferences')).nativeElement as HTMLElement;
    prefTabBtn.click();
    fixture.detectChanges();

    expect(component.activeTab).toBe('preferences');
    expect(fixture.nativeElement.querySelector('#preferencesPane')).toBeTruthy();
  });

  it('5. should copy user ID to clipboard and trigger transient copied state', () => {
    // Mock navigator.clipboard
    const writeTextMock = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, {
      clipboard: {
        writeText: writeTextMock
      }
    });

    const copyBtn = fixture.debugElement.query(By.css('#copyUserIdBtn')).nativeElement as HTMLElement;
    copyBtn.click();
    fixture.detectChanges();

    expect(writeTextMock).toHaveBeenCalledWith('a54ec1f6-4b88-45a0-8b54-2f7110f6c754');
    expect(component.userIdCopied).toBe(true);

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('#copyUserIdBtn')?.textContent).toContain('Copied!');
  });

  it('6. should render factual security posture and masked password in security tab', () => {
    component.setTab('security');
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const text = compiled.textContent || '';

    expect(text).toContain('Active');
    expect(text).toContain('HTTP-Only Cookie');
    expect(text).toContain('PBKDF2-SHA256');
    expect(text).toContain('••••••••••••');
    expect(text).toContain('Passwords are encrypted and salted using PBKDF2-SHA256 server-side');
  });

  it('7. should execute logout on sign out button click', () => {
    component.setTab('security');
    fixture.detectChanges();

    const signOutBtn = fixture.debugElement.query(By.css('#settingsSignOutBtn')).nativeElement as HTMLButtonElement;
    signOutBtn.click();
    fixture.detectChanges();

    expect(mockAuthService.logout).toHaveBeenCalled();
  });

  it('8. should prevent duplicate logout calls while isSigningOut is true', () => {
    component.isSigningOut = true;
    component.logout();

    expect(mockAuthService.logout).not.toHaveBeenCalled();
  });

  it('9. should toggle compact sidebar preference and persist in localStorage', () => {
    component.setTab('preferences');
    fixture.detectChanges();

    expect(component.isCompactSidebar).toBe(false);

    const toggleInput = fixture.debugElement.query(By.css('#compactSidebarToggle')).nativeElement as HTMLInputElement;
    toggleInput.click();
    fixture.detectChanges();

    expect(component.isCompactSidebar).toBe(true);
    expect(localStorage.getItem('easyfin_sidebar_collapsed')).toBe('true');
    expect(component.preferenceSaved).toBe(true);

    // Toggle back
    toggleInput.click();
    fixture.detectChanges();

    expect(component.isCompactSidebar).toBe(false);
    expect(localStorage.getItem('easyfin_sidebar_collapsed')).toBe('false');
  });

  it('10. should display factual Indian accounting standards in preferences', () => {
    component.setTab('preferences');
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const text = compiled.textContent || '';

    expect(text).toContain('01-Apr to 31-Mar (Indian Fiscal Standard)');
    expect(text).toContain('₹ INR (Indian Rupee)');
    expect(text).toContain('1,00,000.00 (Lakhs & Crores)');
    expect(text).toContain('9-Column Accounting Ledger Schema');
  });

  it('11. should NEVER expose sensitive database or server internals in the UI', () => {
    const tabs: Array<'profile' | 'security' | 'preferences'> = ['profile', 'security', 'preferences'];

    for (const tab of tabs) {
      component.setTab(tab);
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent || '';
      expect(text).not.toContain('localhost\\SQLEXPRESS');
      expect(text).not.toContain('EasyFinDbContext');
      expect(text).not.toContain('Microsoft.EntityFrameworkCore');
      expect(text).not.toContain('Preserved Migrations');
      expect(text).not.toContain('Windows Integrated Security');
    }
  });
});
