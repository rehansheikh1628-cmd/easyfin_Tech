import { Component, OnInit, OnDestroy, ChangeDetectorRef } from '@angular/core';
import { AuthService, UserProfileDto } from '../../services/auth.service';
import { Subscription } from 'rxjs';

@Component({
  selector: 'app-app-layout',
  templateUrl: './app-layout.component.html',
  styleUrls: ['./app-layout.component.css'],
  standalone: false
})
export class AppLayoutComponent implements OnInit, OnDestroy {
  sidebarOpen = false;
  currentUser: UserProfileDto | null = null;
  private authSub?: Subscription;

  constructor(
    private authService: AuthService,
    private cdr: ChangeDetectorRef
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

  toggleSidebar(): void {
    this.sidebarOpen = !this.sidebarOpen;
  }

  closeSidebar(): void {
    this.sidebarOpen = false;
  }

  logout(): void {
    this.authService.logout().subscribe();
  }
}
