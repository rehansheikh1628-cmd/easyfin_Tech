import { Component } from '@angular/core';

@Component({
  selector: 'app-public-footer',
  templateUrl: './public-footer.component.html',
  styleUrls: ['./public-footer.component.css'],
  standalone: false
})
export class PublicFooterComponent {
  currentYear = new Date().getFullYear();
}
