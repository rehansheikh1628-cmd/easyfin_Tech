import { Component } from '@angular/core';

@Component({
  selector: 'app-public-nav',
  templateUrl: './public-nav.component.html',
  styleUrls: ['./public-nav.component.css'],
  standalone: false
})
export class PublicNavComponent {
  mobileMenuOpen = false;

  toggleMobileMenu(): void {
    this.mobileMenuOpen = !this.mobileMenuOpen;
  }

  closeMobileMenu(): void {
    this.mobileMenuOpen = false;
  }
}
