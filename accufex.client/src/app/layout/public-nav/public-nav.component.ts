import { Component, HostListener, OnInit, OnDestroy, ChangeDetectorRef } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { AuthService, UserProfileDto } from '../../services/auth.service';
import { Subscription } from 'rxjs';
import { filter } from 'rxjs/operators';

@Component({
  selector: 'app-public-nav',
  templateUrl: './public-nav.component.html',
  styleUrls: ['./public-nav.component.css'],
  standalone: false
})
export class PublicNavComponent implements OnInit, OnDestroy {
  mobileMenuOpen = false;
  productsMegaMenuOpen = false;
  accountMenuOpen = false;
  mobileProductsOpen = true; // Open by default on mobile for easy access
  currentUser: UserProfileDto | null = null;

  private authSub?: Subscription;
  private routerSub?: Subscription;
  private megaMenuTimeout?: any;
  private accountMenuTimeout?: any;

  constructor(
    public authService: AuthService,
    private router: Router,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
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

    // Auto-close open menus on route navigation
    this.routerSub = this.router.events
      .pipe(filter((event) => event instanceof NavigationEnd))
      .subscribe(() => {
        this.closeAllMenus();
        this.cdr.markForCheck();
      });
  }

  ngOnDestroy(): void {
    this.authSub?.unsubscribe();
    this.routerSub?.unsubscribe();
    this.clearTimeouts();
  }

  // Backwards compatibility alias
  get productsDropdownOpen(): boolean {
    return this.productsMegaMenuOpen;
  }
  set productsDropdownOpen(val: boolean) {
    this.productsMegaMenuOpen = val;
  }

  get isAuthenticated(): boolean {
    return !!this.currentUser;
  }

  get userName(): string {
    return this.currentUser?.fullName || 'Finance Team';
  }

  get userRole(): string {
    return this.currentUser?.email || 'Authorized User';
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

  get isProductsActive(): boolean {
    const url = this.router?.url || '';
    return (
      url.startsWith('/converter') ||
      url.startsWith('/excel-to-tally') ||
      url.startsWith('/supported-banks')
    );
  }

  // Mega Menu Hover & Interaction Logic
  onMegaMenuEnter(): void {
    if (this.megaMenuTimeout) {
      clearTimeout(this.megaMenuTimeout);
      this.megaMenuTimeout = undefined;
    }
    this.productsMegaMenuOpen = true;
  }

  onMegaMenuLeave(): void {
    this.megaMenuTimeout = setTimeout(() => {
      this.productsMegaMenuOpen = false;
      this.cdr.markForCheck();
    }, 150);
  }

  toggleProductsDropdown(event?: Event): void {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    this.productsMegaMenuOpen = !this.productsMegaMenuOpen;
    if (this.productsMegaMenuOpen) {
      this.accountMenuOpen = false;
    }
  }

  closeProductsDropdown(): void {
    this.productsMegaMenuOpen = false;
  }

  // Account Menu Interaction Logic
  onAccountMenuEnter(): void {
    if (this.accountMenuTimeout) {
      clearTimeout(this.accountMenuTimeout);
      this.accountMenuTimeout = undefined;
    }
    this.accountMenuOpen = true;
  }

  onAccountMenuLeave(): void {
    this.accountMenuTimeout = setTimeout(() => {
      this.accountMenuOpen = false;
      this.cdr.markForCheck();
    }, 150);
  }

  toggleAccountMenu(event?: Event): void {
    if (event) {
      event.preventDefault();
      event.stopPropagation();
    }
    this.accountMenuOpen = !this.accountMenuOpen;
    if (this.accountMenuOpen) {
      this.productsMegaMenuOpen = false;
    }
  }

  closeAccountMenu(): void {
    this.accountMenuOpen = false;
  }

  // Mobile Menu Logic
  toggleMobileMenu(): void {
    this.mobileMenuOpen = !this.mobileMenuOpen;
    if (this.mobileMenuOpen) {
      this.productsMegaMenuOpen = false;
      this.accountMenuOpen = false;
    }
  }

  closeMobileMenu(): void {
    this.mobileMenuOpen = false;
  }

  toggleMobileProducts(): void {
    this.mobileProductsOpen = !this.mobileProductsOpen;
  }

  closeAllMenus(): void {
    this.mobileMenuOpen = false;
    this.productsMegaMenuOpen = false;
    this.accountMenuOpen = false;
  }

  logout(): void {
    this.closeAllMenus();
    this.authService.logout().subscribe(() => {
      this.router.navigate(['/']);
    });
  }

  private clearTimeouts(): void {
    if (this.megaMenuTimeout) clearTimeout(this.megaMenuTimeout);
    if (this.accountMenuTimeout) clearTimeout(this.accountMenuTimeout);
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    this.productsMegaMenuOpen = false;
    this.accountMenuOpen = false;
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeAllMenus();
  }
}
