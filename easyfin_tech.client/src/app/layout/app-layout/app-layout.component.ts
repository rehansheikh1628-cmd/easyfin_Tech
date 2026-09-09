import { Component, OnInit, OnDestroy, ChangeDetectorRef, HostListener, ElementRef } from '@angular/core';
import { AuthService, UserProfileDto } from '../../services/auth.service';
import { Subscription } from 'rxjs';

@Component({
  selector: 'app-app-layout',
  templateUrl: './app-layout.component.html',
  styleUrls: ['./app-layout.component.css'],
  standalone: false
})
export class AppLayoutComponent implements OnInit, OnDestroy {
  mobileMenuOpen = false;
  profileMenuOpen = false;
  currentUser: UserProfileDto | null = null;
  private authSub?: Subscription;

  constructor(
    private authService: AuthService,
    private cdr: ChangeDetectorRef,
    private elementRef: ElementRef
  ) {}

  ngOnInit(): void {
    this.authSub = this.authService.currentUser$.subscribe((user) => {
      this.currentUser = user;
      this.cdr.markForCheck();
    });

    if (!this.currentUser) {
      this.authService.checkAuth().subscribe();
    }
  }

  ngOnDestroy(): void {
    this.authSub?.unsubscribe();
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

  toggleProfileMenu(event?: Event): void {
    if (event) {
      event.stopPropagation();
    }
    this.profileMenuOpen = !this.profileMenuOpen;
    this.cdr.markForCheck();
  }

  closeProfileMenu(): void {
    if (this.profileMenuOpen) {
      this.profileMenuOpen = false;
      this.cdr.markForCheck();
    }
  }

  toggleMobileMenu(): void {
    this.mobileMenuOpen = !this.mobileMenuOpen;
    if (this.mobileMenuOpen) {
      this.profileMenuOpen = false;
    }
    this.cdr.markForCheck();
  }

  closeMobileMenu(): void {
    this.mobileMenuOpen = false;
    this.cdr.markForCheck();
  }

  // Backwards compatibility alias
  get sidebarOpen(): boolean {
    return this.mobileMenuOpen;
  }
  toggleSidebar(): void {
    this.toggleMobileMenu();
  }
  closeSidebar(): void {
    this.closeMobileMenu();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (this.profileMenuOpen) {
      const target = event.target as HTMLElement;
      if (!this.elementRef.nativeElement.querySelector('.profile-menu-container')?.contains(target)) {
        this.profileMenuOpen = false;
        this.cdr.markForCheck();
      }
    }
  }

  @HostListener('document:keydown.escape')
  onEscapeKey(): void {
    this.closeProfileMenu();
    this.closeMobileMenu();
  }

  logout(): void {
    this.closeProfileMenu();
    this.closeMobileMenu();
    this.authService.logout().subscribe();
  }
}
