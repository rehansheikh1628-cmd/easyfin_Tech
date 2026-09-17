import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { of, BehaviorSubject } from 'rxjs';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { PublicNavComponent } from './public-nav.component';
import { AuthService, UserProfileDto } from '../../services/auth.service';

describe('PublicNavComponent (Navigation Redesign)', () => {
  let component: PublicNavComponent;
  let fixture: ComponentFixture<PublicNavComponent>;
  let currentUserSubject: BehaviorSubject<UserProfileDto | null>;
  let mockAuthService: {
    currentUser$: BehaviorSubject<UserProfileDto | null>;
    isAuthenticated$: BehaviorSubject<boolean>;
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
    currentUserSubject = new BehaviorSubject<UserProfileDto | null>(null);

    mockAuthService = {
      currentUser$: currentUserSubject,
      isAuthenticated$: new BehaviorSubject<boolean>(false),
      checkAuth: vi.fn().mockReturnValue(of(null)),
      logout: vi.fn().mockReturnValue(of({ success: true }))
    };

    await TestBed.configureTestingModule({
      declarations: [PublicNavComponent],
      imports: [CommonModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthService, useValue: mockAuthService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(PublicNavComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create PublicNavComponent with initial state closed', () => {
    expect(component).toBeTruthy();
    expect(component.productsMegaMenuOpen).toBe(false);
    expect(component.accountMenuOpen).toBe(false);
    expect(component.mobileMenuOpen).toBe(false);
    expect(component.mobileProductsOpen).toBe(true);
  });

  it('2. should render brand text without E logo and include company attribution', () => {
    const brandTitle = fixture.debugElement.query(By.css('.brand-title')).nativeElement;
    const brandTag = fixture.debugElement.query(By.css('.brand-tag')).nativeElement;
    const brandCrest = fixture.debugElement.query(By.css('.brand-crest'));

    expect(brandTitle.textContent.trim()).toBe('CompactFin Tech');
    expect(brandTag.textContent.trim()).toBe('A product by EAZ Technologies');
    expect(brandCrest).toBeNull();
  });

  it('3. should toggle products mega menu open and closed', () => {
    const event = new MouseEvent('click');
    component.toggleProductsDropdown(event);
    expect(component.productsMegaMenuOpen).toBe(true);

    component.closeProductsDropdown();
    expect(component.productsMegaMenuOpen).toBe(false);
  });

  it('4. should render Sign In and Open Converter buttons when unauthenticated', () => {
    expect(component.isAuthenticated).toBe(false);

    const signInBtn = fixture.debugElement.query(By.css('#navSignInBtn'));
    const converterBtn = fixture.debugElement.query(By.css('#navOpenConverterBtn'));

    expect(signInBtn).toBeTruthy();
    expect(converterBtn).toBeTruthy();
  });

  it('5. should display user avatar initials and workspace when authenticated', () => {
    currentUserSubject.next(sampleUser);
    fixture.detectChanges();

    expect(component.isAuthenticated).toBe(true);
    expect(component.userName).toBe('Rahul Sharma');
    expect(component.userInitials).toBe('RS');
    expect(component.workspaceName).toBe('Sharma & Associates CA');

    const avatar = fixture.debugElement.query(By.css('.user-avatar-chip'));
    expect(avatar).toBeTruthy();
    expect(avatar.nativeElement.textContent.trim()).toBe('RS');
  });

  it('6. should toggle account dropdown and handle logout', () => {
    currentUserSubject.next(sampleUser);
    fixture.detectChanges();

    const event = new MouseEvent('click');
    component.toggleAccountMenu(event);
    expect(component.accountMenuOpen).toBe(true);

    component.logout();
    expect(component.accountMenuOpen).toBe(false);
    expect(mockAuthService.logout).toHaveBeenCalled();
  });

  it('7. should toggle mobile drawer menu and mobile products accordion', () => {
    expect(component.mobileMenuOpen).toBe(false);
    component.toggleMobileMenu();
    expect(component.mobileMenuOpen).toBe(true);

    expect(component.mobileProductsOpen).toBe(true);
    component.toggleMobileProducts();
    expect(component.mobileProductsOpen).toBe(false);

    component.toggleMobileProducts();
    expect(component.mobileProductsOpen).toBe(true);

    component.closeAllMenus();
    expect(component.mobileMenuOpen).toBe(false);
    expect(component.productsMegaMenuOpen).toBe(false);
    expect(component.accountMenuOpen).toBe(false);
  });

  it('8. should close all open menus on Escape key', () => {
    component.productsMegaMenuOpen = true;
    component.accountMenuOpen = true;
    component.mobileMenuOpen = true;

    component.onEscape();

    expect(component.productsMegaMenuOpen).toBe(false);
    expect(component.accountMenuOpen).toBe(false);
    expect(component.mobileMenuOpen).toBe(false);
  });

  it('9. should render Home navigation link before Products and route to /', () => {
    const desktopNav = fixture.debugElement.query(By.css('.desktop-nav'));
    expect(desktopNav).toBeTruthy();

    const homeLink = fixture.debugElement.query(By.css('#navHomeLink'));
    expect(homeLink).toBeTruthy();
    expect(homeLink.nativeElement.textContent.trim()).toBe('Home');
    expect(homeLink.attributes['routerLink']).toBe('/');

    // Verify Home is the first child or precedes Products in desktop nav
    const desktopChildren = desktopNav.nativeElement.children;
    expect(desktopChildren.length).toBeGreaterThan(1);
    expect(desktopChildren[0].textContent).toContain('Home');
    expect(desktopChildren[1].textContent).toContain('Products');
  });

  it('10. should render exactly two columns in Products mega-menu (Statement Automation & Accounting Workflows)', () => {
    const megaCols = fixture.debugElement.queryAll(By.css('.mega-menu-grid .mega-menu-col'));
    expect(megaCols.length).toBe(2);

    const colTitles = fixture.debugElement.queryAll(By.css('.mega-menu-grid .mega-col-title'));
    expect(colTitles.length).toBe(2);
    expect(colTitles[0].nativeElement.textContent.trim()).toBe('Statement Automation');
    expect(colTitles[1].nativeElement.textContent.trim()).toBe('Accounting Workflows');
  });

  it('11. should NOT contain Workspace column, Dashboard, Files, or Settings in Products mega-menu', () => {
    const megaMenuGrid = fixture.debugElement.query(By.css('.mega-menu-grid')).nativeElement;
    const gridText = megaMenuGrid.textContent;

    expect(gridText).not.toContain('Workspace');
    expect(gridText).not.toContain('My Statements / Files');
    expect(gridText).not.toContain('Dashboard');
    expect(gridText).not.toContain('Settings');
    expect(gridText).not.toContain('Supported Banks');
    expect(gridText).not.toContain('Validation Workflow');

    // Verify Statement Automation items remain
    expect(gridText).toContain('Bank Statement → Excel');

    // Verify Accounting Workflows items remain
    expect(gridText).toContain('Excel → Tally XML');
    expect(gridText).toContain('Bills & Invoices → Excel');

    // Verify authenticated account menu still retains Dashboard, Files, and Settings
    currentUserSubject.next(sampleUser);
    fixture.detectChanges();
    const accountLinks = fixture.debugElement.query(By.css('.account-menu-links')).nativeElement;
    expect(accountLinks.textContent).toContain('Dashboard');
    expect(accountLinks.textContent).toContain('My Statements / Files');
    expect(accountLinks.textContent).toContain('Workspace Settings');
  });
});
