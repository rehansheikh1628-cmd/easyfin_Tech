import { Component, OnInit, OnDestroy, ChangeDetectorRef, HostListener } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { AuthService, UserProfileDto } from '../../services/auth.service';
import { LayoutService } from '../../services/layout.service';
import { Subscription } from 'rxjs';
import { filter } from 'rxjs/operators';

@Component({
  selector: 'app-app-layout',
  templateUrl: './app-layout.component.html',
  styleUrls: ['./app-layout.component.css'],
  standalone: false
})
export class AppLayoutComponent implements OnInit, OnDestroy {
  sidebarOpen = false;
  isCollapsed = false;
  profileMenuOpen = false;
  currentUser: UserProfileDto | null = null;
  private authSub?: Subscription;
  private routerSub?: Subscription;
  private layoutSub?: Subscription;

  constructor(
    private authService: AuthService,
    private layoutService: LayoutService,
    private router: Router,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.isCollapsed = this.layoutService.isCollapsed;
    this.layoutSub = this.layoutService.isCollapsed$.subscribe((collapsed) => {
      this.isCollapsed = collapsed;
      this.cdr.markForCheck();
    });

    this.authSub = this.authService.currentUser$.subscribe((user) => {
      this.currentUser = user;
      this.cdr.markForCheck();
    });

    if (!this.currentUser) {
      this.authService.checkAuth().subscribe();
    }

    // Auto-close mobile drawer and dropdown on route change
    this.routerSub = this.router.events
      .pipe(filter((event) => event instanceof NavigationEnd))
      .subscribe(() => {
        this.sidebarOpen = false;
        this.profileMenuOpen = false;
        this.cdr.markForCheck();
      });
  }

  ngOnDestroy(): void {
    this.authSub?.unsubscribe();
    this.routerSub?.unsubscribe();
    this.layoutSub?.unsubscribe();
  }

  get userName(): string {
    return this.currentUser?.fullName || 'Finance Team';
  }

  get userRole(): string {
    return this.currentUser?.email || 'Authenticated User';
  }

  get workspaceName(): string {
    return this.currentUser?.workspaceName || 'My Workspace';
  }

  get userInitials(): string {
    if (this.currentUser?.fullName) {
      const parts = this.currentUser.fullName.trim().split(/\s+/);
      if (parts.length >= 2) {
        return (parts[0][0] + parts[1][0]).toUpperCase();
      }
      return parts[0].substring(0, 2).toUpperCase();
    }
    if (this.currentUser?.email) {
      return this.currentUser.email.substring(0, 2).toUpperCase();
    }
    return 'EF';
  }

  get breadcrumbCategory(): string {
    const url = this.router.url.split('?')[0];
    if (url.includes('/dashboard')) return 'Workspace';
    if (url.includes('/converter') || url.includes('/excel-to-tally')) return 'Core Workflow';
    if (url.includes('/files') || url.includes('/supported-banks')) return 'Tools';
    if (url.includes('/settings')) return 'Account';
    return 'Workspace';
  }

  get breadcrumbTitle(): string {
    const url = this.router.url.split('?')[0];
    if (url.includes('/dashboard')) return 'Dashboard';
    if (url.includes('/converter')) return 'Bank Statement → Excel';
    if (url.includes('/excel-to-tally')) return 'Excel → Tally XML';
    if (url.includes('/files')) return 'My Statements & Spreadsheets';
    if (url.includes('/settings')) return 'Workspace Settings';
    if (url.includes('/supported-banks')) return 'Supported Banks';
    return 'Financial Workspace';
  }

  toggleSidebar(): void {
    this.sidebarOpen = !this.sidebarOpen;
  }

  closeSidebar(): void {
    this.sidebarOpen = false;
  }

  toggleCollapse(): void {
    this.layoutService.toggleCollapse();
  }

  toggleProfileMenu(event: Event): void {
    event.stopPropagation();
    this.profileMenuOpen = !this.profileMenuOpen;
  }

  closeProfileMenu(): void {
    this.profileMenuOpen = false;
  }

  logout(): void {
    this.profileMenuOpen = false;
    this.authService.logout().subscribe();
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    this.profileMenuOpen = false;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.profileMenuOpen = false;
    this.sidebarOpen = false;
  }
}
