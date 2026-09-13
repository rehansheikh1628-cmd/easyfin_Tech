import { Component, OnInit, OnDestroy, ChangeDetectorRef } from '@angular/core';
import { Subscription } from 'rxjs';
import { AuthService, UserProfileDto } from '../../services/auth.service';
import { LayoutService } from '../../services/layout.service';

@Component({
  selector: 'app-settings',
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.css'],
  standalone: false
})
export class SettingsComponent implements OnInit, OnDestroy {
  activeTab: 'profile' | 'security' | 'preferences' = 'profile';
  currentUser: UserProfileDto | null = null;
  userIdCopied = false;
  isCompactSidebar = false;
  preferenceSaved = false;
  isSigningOut = false;

  private authSub?: Subscription;
  private layoutSub?: Subscription;
  private copyTimeoutId?: ReturnType<typeof setTimeout>;
  private prefTimeoutId?: ReturnType<typeof setTimeout>;

  constructor(
    public authService: AuthService,
    private layoutService: LayoutService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.isCompactSidebar = this.layoutService.isCollapsed;
    this.layoutSub = this.layoutService.isCollapsed$.subscribe((collapsed) => {
      this.isCompactSidebar = collapsed;
      this.cdr.markForCheck();
    });

    this.currentUser = this.authService.currentUser;
    this.authSub = this.authService.currentUser$.subscribe((user) => {
      this.currentUser = user;
      this.cdr.markForCheck();
    });

    if (!this.currentUser) {
      this.authService.checkAuth().subscribe((user) => {
        this.currentUser = user;
        this.cdr.markForCheck();
      });
    }
  }

  ngOnDestroy(): void {
    this.authSub?.unsubscribe();
    this.layoutSub?.unsubscribe();
    if (this.copyTimeoutId) clearTimeout(this.copyTimeoutId);
    if (this.prefTimeoutId) clearTimeout(this.prefTimeoutId);
  }

  setTab(tab: 'profile' | 'security' | 'preferences'): void {
    this.activeTab = tab;
    this.cdr.markForCheck();
  }

  get userInitials(): string {
    const name = this.currentUser?.fullName || this.currentUser?.workspaceName || this.currentUser?.email || 'EF';
    const parts = name.trim().split(/\s+/);
    if (parts.length >= 2) {
      return (parts[0][0] + parts[1][0]).toUpperCase();
    }
    return name.slice(0, 2).toUpperCase();
  }

  get effectiveUserId(): string {
    return this.currentUser?.id || this.currentUser?.userId || 'N/A';
  }

  copyUserId(): void {
    const id = this.effectiveUserId;
    if (!id || id === 'N/A') return;

    this.triggerCopiedState();
    if (navigator?.clipboard?.writeText) {
      navigator.clipboard.writeText(id).catch(() => {
        // Clipboard access might be blocked in non-secure or test contexts
      });
    }
  }

  private triggerCopiedState(): void {
    this.userIdCopied = true;
    if (this.copyTimeoutId) clearTimeout(this.copyTimeoutId);
    this.copyTimeoutId = setTimeout(() => {
      this.userIdCopied = false;
      this.cdr.markForCheck();
    }, 2000);
    this.cdr.markForCheck();
  }

  toggleCompactSidebar(): void {
    this.isCompactSidebar = !this.isCompactSidebar;
    this.layoutService.setCollapsed(this.isCompactSidebar);

    this.preferenceSaved = true;
    if (this.prefTimeoutId) clearTimeout(this.prefTimeoutId);
    this.prefTimeoutId = setTimeout(() => {
      this.preferenceSaved = false;
      this.cdr.markForCheck();
    }, 2000);
    this.cdr.markForCheck();
  }

  logout(): void {
    if (this.isSigningOut) return;
    this.isSigningOut = true;
    this.authService.logout().subscribe({
      next: () => {
        this.isSigningOut = false;
        this.cdr.markForCheck();
      },
      error: () => {
        this.isSigningOut = false;
        this.cdr.markForCheck();
      }
    });
  }
}
